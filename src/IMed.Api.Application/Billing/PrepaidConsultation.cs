using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Application.Billing;

/// <summary>Lo que se cobra al vender la consulta de una cita.</summary>
/// <param name="LocationId">Sede de la cita (participante <c>Location/sede-…</c>), si la trae.</param>
public sealed record AppointmentConsultation(
    string AppointmentId,
    string PatientFhirId,
    string? PractitionerId,
    DateTimeOffset? Start,
    string Status,
    string SpecialtyCode,
    ConsultationType Type,
    BillableItemDto Item,
    Guid? LocationId);

/// <summary>
/// Qué consulta corresponde a una cita, con las mismas reglas que el cobro de la consulta al
/// iniciarla (<see cref="EncounterChargeReconciler"/>): la especialidad es la del médico (o
/// medicina general), el tipo es el que se eligió al agendar (o el propuesto), y el cobrable es el
/// de esa especialidad y ese tipo.
///
/// <para>Usar las mismas reglas es lo que hace que la consulta precobrada y la que se cobraría al
/// iniciar sean la misma: si se calcularan distinto, el paciente pagaría una cosa en caja y el
/// médico atendería otra.</para>
/// </summary>
public sealed class AppointmentConsultationResolver(
    IHapiFhirClient hapi,
    IBillableItemProvisioner provisioner,
    PractitionerSpecialtyLookup specialties,
    SuggestConsultationTypeHandler suggester)
{
    /// <summary>Estados de una cita que todavía se puede cobrar por adelantado.</summary>
    public static readonly IReadOnlySet<string> Cobrables =
        new HashSet<string>(["proposed", "pending", "booked", "arrived", "checked-in"], StringComparer.Ordinal);

    public async Task<AppointmentConsultation?> ResolveAsync(Guid orgId, string appointmentId, CancellationToken ct = default)
    {
        var particion = $"org-{orgId}";
        var cita = await hapi.ReadResourceAsync("Appointment", appointmentId, particion, ct);
        return cita is null ? null : await ResolveAsync(orgId, cita, ct);
    }

    public async Task<AppointmentConsultation?> ResolveAsync(Guid orgId, JsonObject cita, CancellationToken ct = default)
    {
        var id = cita["id"]?.GetValue<string>();
        var actores = cita["participant"]?.AsArray()
            .Select(p => p?["actor"]?["reference"]?.GetValue<string>())
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList() ?? [];
        var paciente = Id(cita["subject"]?["reference"]?.GetValue<string>(), "Patient")
            ?? actores.Select(r => Id(r, "Patient")).FirstOrDefault(x => x is not null);
        if (id is null || paciente is null)
        {
            return null;
        }
        var medico = actores.Select(r => Id(r, "Practitioner")).FirstOrDefault(x => x is not null);
        var sede = actores.Select(SedeDe).FirstOrDefault(x => x is not null);

        var especialidad = await specialties.FindAsync(orgId, medico, ct) ?? ConsultationTypeCodes.GeneralPracticeSpecialty;
        var tipo = cita["appointmentType"]?["coding"]?.AsArray()
                .Where(c => c?["system"]?.GetValue<string>() == ConsultationTypeCodes.System)
                .Select(c => ConsultationTypeCodes.FromCode(c?["code"]?.GetValue<string>()))
                .FirstOrDefault(t => t is not null)
            ?? ConsultationTypeCodes.FromCode((await suggester.HandleAsync(orgId, paciente, especialidad, null, ct)).Type)!.Value;

        var items = await provisioner.EnsureForSpecialtyAsync(orgId, especialidad, ct);
        var item = items.FirstOrDefault(i => i.ConsultationType == tipo.ToString());
        if (item is null)
        {
            return null;
        }
        return new AppointmentConsultation(
            id, paciente, medico,
            DateTimeOffset.TryParse(cita["start"]?.GetValue<string>(), out var inicio) ? inicio : null,
            cita["status"]?.GetValue<string>() ?? string.Empty,
            especialidad, tipo, item, sede);
    }

    private static string? Id(string? reference, string type) =>
        reference is not null && reference.StartsWith(type + "/", StringComparison.Ordinal)
            ? reference[(type.Length + 1)..]
            : null;

    private static Guid? SedeDe(string reference) =>
        reference.StartsWith("Location/sede-", StringComparison.Ordinal)
        && Guid.TryParseExact(reference["Location/sede-".Length..], "N", out var g)
            ? g : null;
}
