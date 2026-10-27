using System.Globalization;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Pharmacy;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Inventory;

/// <summary>
/// Las recetas de las consultas, leídas de HAPI (B4). Se arma sobre las lecturas genéricas del
/// cliente compartido (<see cref="IHapiFhirClient.SearchResourcesAsync"/> y
/// <see cref="IHapiFhirClient.ReadResourceAsync"/>) para no tocar ese archivo: lo que la farmacia
/// necesita leer de un <c>MedicationRequest</c> se interpreta aquí.
/// </summary>
public sealed class HapiPrescriptionSource(IHapiFhirClient hapi, ILogger<HapiPrescriptionSource> logger) : IPrescriptionSource
{
    private const string AtcSystem = "http://www.whocc.no/atc";

    /// <summary>Tope de la cola: más de esto en el rango pedido es señal de un rango mal elegido.</summary>
    private const int MaxResults = 300;

    private static string Partition(Guid organizationId) => $"org-{organizationId}";

    public async Task<IReadOnlyList<PrescribedMedication>> ListActiveAsync(
        Guid organizationId, DateTimeOffset since, CancellationToken ct = default)
    {
        var desde = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var recursos = await hapi.SearchResourcesAsync(
            "MedicationRequest",
            $"status=active&authoredon=ge{Uri.EscapeDataString(desde)}&_sort=-authoredon&_count={MaxResults}",
            Partition(organizationId), ct);
        return recursos.Select(Parse).Where(r => r is not null).Select(r => r!).ToList();
    }

    public async Task<PrescribedMedication?> GetAsync(
        Guid organizationId, string medicationRequestId, CancellationToken ct = default)
    {
        var recurso = await hapi.ReadResourceAsync("MedicationRequest", medicationRequestId, Partition(organizationId), ct);
        return recurso is null ? null : Parse(recurso);
    }

    public async Task<IReadOnlyDictionary<string, Guid?>> GetEncounterLocationsAsync(
        Guid organizationId, IReadOnlyCollection<string> encounterIds, CancellationToken ct = default)
    {
        var particion = Partition(organizationId);
        var resultado = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        // Lecturas por id, de a pocas en paralelo: la cola rara vez pasa de unas decenas de consultas.
        foreach (var tanda in encounterIds.Chunk(8))
        {
            var lecturas = await Task.WhenAll(tanda.Select(async id =>
            {
                try
                {
                    return (id, await hapi.ReadResourceAsync("Encounter", id, particion, ct));
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "No se pudo leer el encuentro {EncounterId} para la cola de farmacia.", id);
                    return (id, (JsonObject?)null);
                }
            }));
            foreach (var (id, enc) in lecturas)
            {
                resultado[id] = SedeDe(enc);
            }
        }
        return resultado;
    }

    public async Task<IReadOnlyDictionary<string, string?>> GetPatientNamesAsync(
        Guid organizationId, IReadOnlyCollection<string> patientIds, CancellationToken ct = default)
    {
        var particion = Partition(organizationId);
        var resultado = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tanda in patientIds.Chunk(8))
        {
            var lecturas = await Task.WhenAll(tanda.Select(async id =>
            {
                try
                {
                    return (id, (await hapi.GetPatientAsync(id, particion, ct))?.DisplayName);
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "No se pudo leer el paciente {PatientId} para la cola de farmacia.", id);
                    return (id, (string?)null);
                }
            }));
            foreach (var (id, nombre) in lecturas)
            {
                resultado[id] = nombre;
            }
        }
        return resultado;
    }

    private static Guid? SedeDe(JsonObject? encounter)
    {
        if (encounter?["location"] is not JsonArray ubicaciones)
        {
            return null;
        }
        foreach (var u in ubicaciones)
        {
            if (PharmacyLocationRefs.Parse((string?)u?["location"]?["reference"]) is { } sede)
            {
                return sede;
            }
        }
        return null;
    }

    /// <summary>Interpreta un <c>MedicationRequest</c> R5 (y el <c>medicationCodeableConcept</c> legado).</summary>
    internal static PrescribedMedication? Parse(JsonObject r)
    {
        var id = (string?)r["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        var concepto = r["medication"]?["concept"] as JsonObject ?? r["medicationCodeableConcept"] as JsonObject;
        string? atc = null, display = null;
        if (concepto?["coding"] is JsonArray codings)
        {
            var coding = codings.OfType<JsonObject>()
                .FirstOrDefault(c => (string?)c["system"] == AtcSystem) ?? codings.OfType<JsonObject>().FirstOrDefault();
            atc = (string?)coding?["code"];
            display = (string?)coding?["display"];
        }
        decimal? cantidad = null;
        if (r["dispenseRequest"]?["quantity"]?["value"] is JsonValue v && v.TryGetValue<decimal>(out var q))
        {
            cantidad = q;
        }
        DateTimeOffset? emitida = DateTimeOffset.TryParse(
            (string?)r["authoredOn"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var a) ? a : null;

        return new PrescribedMedication(
            id,
            Referencia((string?)r["subject"]?["reference"], "Patient/") ?? string.Empty,
            Referencia((string?)r["encounter"]?["reference"], "Encounter/"),
            (string?)r["status"] ?? "unknown",
            string.IsNullOrWhiteSpace(atc) ? null : atc.Trim().ToUpperInvariant(),
            display,
            (string?)concepto?["text"],
            (string?)r["dosageInstruction"]?[0]?["text"],
            cantidad,
            (string?)r["dispenseRequest"]?["quantity"]?["unit"],
            (string?)r["groupIdentifier"]?["value"],
            Referencia((string?)r["requester"]?["reference"], "Practitioner/"),
            emitida);
    }

    private static string? Referencia(string? reference, string prefix) =>
        string.IsNullOrWhiteSpace(reference) ? null
        : reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : reference;
}
