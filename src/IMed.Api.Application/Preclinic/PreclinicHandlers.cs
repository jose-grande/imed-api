using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Preclinic;

/// <summary>
/// Lo que ambos handlers leen antes de actuar: la cita, su paciente, el encuentro abierto de la
/// cita (si existe) y las alergias activas. La cita tiene que estar en la fila de preclínica: es lo
/// que acota el permiso <c>record.preclinic</c> a "el paciente que tengo enfrente".
/// </summary>
internal sealed record PreclinicSnapshot(
    JsonObject Appointment,
    string PatientId,
    string? EncounterId,
    IReadOnlyList<PreclinicFhir.ExistingAllergy> Allergies,
    IReadOnlyList<PreclinicFhir.ExistingMedication> Medications);

internal static class PreclinicLoader
{
    public static string PartitionOf(Guid organizationId) => $"org-{organizationId}";

    public static async Task<PreclinicSnapshot> LoadAsync(
        IHapiFhirClient hapi, string partition, string appointmentId, CancellationToken ct)
    {
        var appointment = await hapi.ReadResourceAsync("Appointment", appointmentId, partition, ct)
            ?? throw new PreclinicAppointmentNotFoundException(appointmentId);

        var status = (string?)appointment["status"] ?? "unknown";
        if (!PreclinicFhir.QueueStatuses.Contains(status))
        {
            throw new PreclinicAppointmentNotInQueueException(status);
        }

        var patientId = PreclinicFhir.PatientIdOf(appointment)
            ?? throw new ArgumentException("La cita no tiene paciente asociado.");

        // `planned` lo deja una preclínica anterior de esta misma cita (repetir la toma es válido,
        // J-D6); `in-progress` significa que el médico ya entró, y la toma se suma a su consulta.
        var encounters = await hapi.SearchResourcesAsync(
            "Encounter",
            $"appointment=Appointment/{Uri.EscapeDataString(appointmentId)}&status=planned,in-progress&_count=5",
            partition, ct);
        var encounterId = encounters
            .OrderBy(e => (string?)e["status"] == "in-progress" ? 0 : 1)
            .Select(e => (string?)e["id"])
            .FirstOrDefault(id => id is not null);

        var allergyResources = await hapi.SearchResourcesAsync(
            "AllergyIntolerance",
            $"patient=Patient/{Uri.EscapeDataString(patientId)}&clinical-status=active&_count=100",
            partition, ct);
        var allergies = allergyResources
            .Select(PreclinicFhir.ParseAllergy)
            .Where(a => a is not null && a.VerificationStatus != "entered-in-error")
            .Select(a => a!)
            .ToList();

        var medicationResources = await hapi.SearchResourcesAsync(
            "MedicationStatement",
            $"subject=Patient/{Uri.EscapeDataString(patientId)}&status=recorded,draft&_count=100",
            partition, ct);
        var medications = medicationResources
            .Select(PreclinicFhir.ParseMedication)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        return new PreclinicSnapshot(appointment, patientId, encounterId, allergies, medications);
    }
}

/// <summary><c>GET /api/preclinic/appointments/{id}</c>: lo que la auxiliar ve antes de capturar.</summary>
public sealed class GetPreclinicContextHandler(IHapiFhirClient hapi)
{
    public async Task<PreclinicContextDto> HandleAsync(
        Guid organizationId, string appointmentId, CancellationToken ct = default)
    {
        var partition = PreclinicLoader.PartitionOf(organizationId);
        var snap = await PreclinicLoader.LoadAsync(hapi, partition, appointmentId, ct);

        var vitals = new List<PreclinicVitalDto>();
        if (snap.EncounterId is not null)
        {
            var observations = await hapi.SearchResourcesAsync(
                "Observation",
                $"encounter=Encounter/{Uri.EscapeDataString(snap.EncounterId)}&category=vital-signs&_count=50",
                partition, ct);
            vitals.AddRange(observations.Select(PreclinicFhir.ParseVital).Where(v => v is not null).Select(v => v!));
        }

        var allergies = snap.Allergies
            .Where(a => !a.IsNegation)
            .Select(a =>
            {
                var snomed = a.Codings.FirstOrDefault(c => c.System == PreclinicFhir.SnomedSystem);
                return new PreclinicAllergyDto(a.Id, snomed.Code, snomed.System, a.Display, a.Category, a.VerificationStatus);
            })
            .ToList();

        // La identificación del paciente viaja acá y no por el proxy: la auxiliar no tiene lectura
        // del expediente, y pedirle el Patient entero para mostrar un nombre es justo lo que SEC-4
        // va a negar al pasar a `Enforce`.
        var patient = await hapi.GetPatientAsync(snap.PatientId, partition, ct);

        return new PreclinicContextDto(
            appointmentId,
            (string?)snap.Appointment["status"] ?? "unknown",
            (string?)snap.Appointment["start"],
            snap.PatientId,
            patient?.DisplayName,
            patient?.IdentifierValue,
            snap.EncounterId,
            NoKnownAllergies: allergies.Count == 0 && snap.Allergies.Any(a => a.IsNoKnownAllergy),
            allergies,
            vitals.OrderByDescending(v => v.EffectiveAt).ToList(),
            snap.Medications
                .Select(m => new PreclinicMedicationDto(m.Id, m.AtcCode, m.Display, m.DosageText, m.Status))
                .ToList());
    }
}

/// <summary>
/// <c>POST /api/preclinic/appointments/{id}</c>: escribe la preclínica en una sola transacción —
/// encuentro, signos vitales, alergias y la cita a <c>checked-in</c>—. Reemplaza el envío del
/// navegador, que escribía cada recurso por separado y sin poder ver lo ya registrado.
/// </summary>
public sealed class SubmitPreclinicHandler(IHapiFhirClient hapi, TimeProvider timeProvider)
{
    public async Task<PreclinicSubmitResultDto> HandleAsync(SubmitPreclinicCommand cmd, CancellationToken ct = default)
    {
        var partition = PreclinicLoader.PartitionOf(cmd.OrganizationId);
        var snap = await PreclinicLoader.LoadAsync(hapi, partition, cmd.AppointmentId, ct);

        var plan = PreclinicFhir.Build(
            snap.Appointment, snap.PatientId, snap.EncounterId, snap.Allergies, cmd, timeProvider.GetUtcNow(),
            snap.Medications);

        var results = await hapi.ExecuteTransactionAsync(plan.Bundle, partition, ct);

        // El encuentro es la primera entrada cuando se crea; si no, ya se conocía.
        var encounterId = plan.CreatesEncounter
            ? results.Count > 0 && results[0].ResourceType == "Encounter" ? results[0].ResourceId : null
            : snap.EncounterId;
        if (encounterId is null)
        {
            throw new InvalidOperationException("HAPI no devolvió la ubicación del encuentro creado.");
        }

        return new PreclinicSubmitResultDto(
            encounterId,
            plan.ObservationCount,
            plan.AllergyCount,
            plan.MedicationCount,
            plan.SkippedDuplicates,
            plan.RecordsNoKnownAllergies,
            AppointmentCheckedIn: true);
    }
}
