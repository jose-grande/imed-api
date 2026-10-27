using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>
/// Toda cita y todo encuentro ocurren en una sede, y el médico que atiende está asignado a ella
/// (FAC-9, FAC9-DEC-02/03).
///
/// <para><b>Por qué en el proxy.</b> El frontend escribe <c>Appointment</c> y <c>Encounter</c>
/// directo a HAPI a través del proxy; no hay endpoint de negocio por el que pasen. Y la sede no es
/// un dato decorativo: decide con qué razón social se factura lo que se atiende, así que una cita
/// sin sede —o en una sede donde el médico no trabaja— es un cobro que después nadie sabe a quién
/// imputar.</para>
///
/// <para><b>Altas sí, actualizaciones solo si declaran sede.</b> Los encuentros y citas creados
/// antes de FAC-9 no tienen sede; exigirla al actualizarlos dejaría sin poder cerrar una consulta
/// que ya estaba en curso el día del despliegue. Si la actualización trae sede, se valida igual.</para>
/// </summary>
public sealed class FhirLocationGuard(ILocationRepository locations)
{
    public const string SedePrefix = "Location/sede-";

    public static bool Applies(FhirRequestDescriptor request) =>
        request.Verb is FhirVerb.Create or FhirVerb.Update
        && request.ResourceType is "Encounter" or "Appointment";

    public async Task<FhirAccessDecision?> CheckAsync(
        FhirCallerContext caller, FhirRequestDescriptor request, string? body, CancellationToken ct)
    {
        if (!Applies(request))
        {
            return null;
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement;
        }
        catch (JsonException)
        {
            return FhirAccessDecision.Deny("El cuerpo no es JSON válido.", request.ResourceType);
        }

        var esCita = request.ResourceType == "Appointment";
        var sedesRef = esCita ? AppointmentLocations(root) : EncounterLocations(root);
        var que = esCita ? "La cita" : "El encuentro";

        if (sedesRef.Count == 0)
        {
            return request.Verb == FhirVerb.Create
                ? FhirAccessDecision.Deny(
                    $"{que} tiene que indicar la sede donde se atiende. Elegí la sede activa y volvé a intentar.",
                    request.ResourceType)
                : null;
        }
        if (sedesRef.Count > 1)
        {
            return FhirAccessDecision.Deny($"{que} se atiende en una sola sede.", request.ResourceType);
        }

        var sedeId = ParseSedeId(sedesRef[0]);
        var sede = sedeId is null ? null : await locations.GetAsync(caller.OrganizationId, sedeId.Value, ct);
        if (sede is null || !sede.Active)
        {
            return FhirAccessDecision.Deny(
                $"{que} indica una sede que no existe o está inactiva.", request.ResourceType);
        }

        foreach (var practitionerId in Practitioners(root))
        {
            var asignado = await locations.IsPractitionerAssignedAsync(caller.OrganizationId, practitionerId, sede.Id, ct);
            if (asignado == false)
            {
                return FhirAccessDecision.Deny(
                    $"El profesional no está asignado a la sede {sede.Name}. " +
                    "Asignalo a esa sede desde Equipo, o atendé desde una de sus sedes.",
                    request.ResourceType);
            }
        }

        return null;
    }

    /// <summary>Id de la sede a partir de la referencia <c>Location/sede-{guid:N}</c>.</summary>
    public static Guid? ParseSedeId(string reference) =>
        reference.StartsWith(SedePrefix, StringComparison.Ordinal)
        && Guid.TryParseExact(reference[SedePrefix.Length..], "N", out var id)
            ? id
            : null;

    private static List<string> EncounterLocations(JsonElement root)
    {
        var result = new List<string>();
        if (root.TryGetProperty("location", out var locs) && locs.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in locs.EnumerateArray())
            {
                if (l.TryGetProperty("location", out var loc) && Reference(loc) is { } r
                    && r.StartsWith("Location/", StringComparison.Ordinal))
                {
                    result.Add(r);
                }
            }
        }
        return result;
    }

    private static List<string> AppointmentLocations(JsonElement root) =>
        ParticipantActors(root).Where(r => r.StartsWith("Location/", StringComparison.Ordinal)).ToList();

    /// <summary>Encounter y Appointment R5 nombran igual al actor: <c>participant[].actor</c>.</summary>
    private static IEnumerable<string> Practitioners(JsonElement root) =>
        ParticipantActors(root)
            .Where(r => r.StartsWith("Practitioner/", StringComparison.Ordinal))
            .Select(r => r["Practitioner/".Length..])
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<string> ParticipantActors(JsonElement root)
    {
        if (!root.TryGetProperty("participant", out var participants) || participants.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }
        foreach (var p in participants.EnumerateArray())
        {
            if (p.TryGetProperty("actor", out var actor) && Reference(actor) is { } r)
            {
                yield return r;
            }
        }
    }

    private static string? Reference(JsonElement el) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty("reference", out var r) && r.ValueKind == JsonValueKind.String
            ? r.GetString()
            : null;
}
