using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabSpecimens;

// -------------------- DTOs (R5: madurez preanalítica) --------------------

public sealed record AliquotResult(string Accession, string SpecimenFhirId);

/// <summary>Estado de estabilidad de una muestra para add-on (LIS-RD-21).</summary>
public sealed record StabilityView(
    bool WithinStability,
    int? StabilityMinutes,
    double ElapsedMinutes,
    string? SpecimenTypeCode);

/// <summary>
/// Alícuotas (R5, LIS-RD-20): divide una muestra recibida en N recipientes
/// derivados (`Specimen.parent`), con accession `{padre}-A{i}`, y registra el
/// evento `Aliquoted`.
/// </summary>
public sealed class CreateAliquotHandler(IHapiFhirClient hapi, ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AliquotResult>> HandleAsync(
        Guid orgId, Guid intakeId, int count, Guid userId, string? note, CancellationToken ct = default)
    {
        var intake = await ops.GetIntakeByIdAsync(intakeId, ct);
        if (intake is null || intake.OrganizationId != orgId)
        {
            throw new KeyNotFoundException($"Intake '{intakeId}' no existe.");
        }
        if (string.IsNullOrWhiteSpace(intake.SpecimenFhirId))
        {
            throw new InvalidOperationException("La orden no tiene una muestra recibida para alicuotar.");
        }

        var partition = $"org-{orgId}";
        var n = Math.Clamp(count, 1, 20);
        var now = timeProvider.GetUtcNow();
        var results = new List<AliquotResult>();
        for (var i = 1; i <= n; i++)
        {
            var accession = $"{intake.AccessionNumber}-A{i}";
            var id = await hapi.CreateSpecimenAsync(new UpsertSpecimenRequest(
                PartitionName: partition,
                PatientFhirId: intake.PatientFhirId,
                ServiceRequestFhirId: intake.ServiceRequestFhirId,
                Status: "available",
                TypeText: intake.SpecimenTypeCode,
                AccessionNumber: accession,
                ReceivedTime: now.ToString("o"),
                CollectedDateTime: null,
                FastingHours: null,
                Note: note,
                ParentSpecimenFhirId: intake.SpecimenFhirId), ct);
            results.Add(new AliquotResult(accession, id));
        }

        await ops.AddSpecimenEventAsync(
            SpecimenEvent.Create(intake.Id, SpecimenEventType.Aliquoted, userId, notes: $"{n} alícuota(s)", timeProvider: timeProvider), ct);
        return results;
    }
}

/// <summary>
/// Almacenamiento / descarte (R5, LIS-RD-21): registra un evento `Stored` con la
/// ubicación física (o el descarte) de la muestra.
/// </summary>
public sealed class RecordStorageHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task HandleAsync(
        Guid orgId, Guid intakeId, string location, bool disposal, Guid userId, string? note, CancellationToken ct = default)
    {
        var intake = await ops.GetIntakeByIdAsync(intakeId, ct);
        if (intake is null || intake.OrganizationId != orgId)
        {
            throw new KeyNotFoundException($"Intake '{intakeId}' no existe.");
        }
        var label = disposal ? "Descarte" : "Almacenamiento";
        var notes = $"{label}: {location}" + (string.IsNullOrWhiteSpace(note) ? string.Empty : $" — {note}");
        await ops.AddSpecimenEventAsync(
            SpecimenEvent.Create(intake.Id, SpecimenEventType.Stored, userId, notes: notes, timeProvider: timeProvider), ct);
    }
}

/// <summary>
/// Chequeo de estabilidad (R5, LIS-RD-21): ¿la muestra sigue dentro de su ventana
/// de estabilidad (para permitir add-on)? Compara el tiempo transcurrido desde la
/// recepción contra <c>specimen_type.stability_minutes</c>.
/// </summary>
public sealed class CheckStabilityHandler(
    ILabOperationsRepository ops, ILabCatalogRepository catalog, TimeProvider timeProvider)
{
    public async Task<StabilityView?> HandleAsync(Guid orgId, Guid intakeId, CancellationToken ct = default)
    {
        var intake = await ops.GetIntakeByIdAsync(intakeId, ct);
        if (intake is null || intake.OrganizationId != orgId)
        {
            return null;
        }

        int? stability = null;
        if (!string.IsNullOrWhiteSpace(intake.SpecimenTypeCode))
        {
            var types = await catalog.ListSpecimenTypesAsync(orgId, ct);
            stability = types.FirstOrDefault(s => s.Code == intake.SpecimenTypeCode)?.StabilityMinutes;
        }

        var elapsed = intake.ReceivedAt is { } r ? (timeProvider.GetUtcNow() - r).TotalMinutes : 0;
        var within = stability is null || elapsed <= stability.Value;
        return new StabilityView(within, stability, Math.Round(elapsed, 1), intake.SpecimenTypeCode);
    }
}
