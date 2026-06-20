using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>Fila de la worklist operacional (Épica LIS-3, L3-F3).</summary>
public sealed record WorklistItem(
    Guid IntakeId,
    string ServiceRequestId,
    string? SpecimenFhirId,
    string PatientFhirId,
    string? AccessionNumber,
    string Priority,
    string Status,
    DateTimeOffset? ReceivedAt,
    int? ElapsedMinutes,
    int? TargetMinutes,
    bool TatBreached);

public sealed record WorklistQuery(Guid OrganizationId, string? Status);

/// <summary>
/// Worklist server-side (L3-D9): intakes in-house por estado/prioridad con TAT
/// transcurrido desde la recepción (LIS-DEC-06). El objetivo de TAT usa un default
/// por prioridad; el objetivo fino por prueba (`tat_target`) queda como follow-up
/// (requiere resolver el código de prueba de cada orden — N+1 contra HAPI).
/// </summary>
public sealed class GetWorklistHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<WorklistItem>> HandleAsync(WorklistQuery query, CancellationToken ct = default)
    {
        OrderIntakeStatus? status = Enum.TryParse<OrderIntakeStatus>(query.Status, ignoreCase: true, out var s) ? s : null;
        var rows = await ops.ListInHouseIntakesAsync(query.OrganizationId, status, ct);
        var now = timeProvider.GetUtcNow();

        return rows.Select(x =>
        {
            int? elapsed = x.ReceivedAt is { } r ? (int)Math.Max(0, (now - r).TotalMinutes) : null;
            var target = DefaultTargetMinutes(x.Priority);
            var breached = elapsed is { } e && e > target;
            return new WorklistItem(
                IntakeId: x.Id,
                ServiceRequestId: x.ServiceRequestFhirId,
                SpecimenFhirId: x.SpecimenFhirId,
                PatientFhirId: x.PatientFhirId,
                AccessionNumber: x.AccessionNumber,
                Priority: x.Priority.ToString(),
                Status: x.Status.ToString(),
                ReceivedAt: x.ReceivedAt,
                ElapsedMinutes: elapsed,
                TargetMinutes: target,
                TatBreached: breached);
        }).ToList();
    }

    private static int DefaultTargetMinutes(LabRequestPriority priority) => priority switch
    {
        LabRequestPriority.Stat => 30,
        LabRequestPriority.Asap => 45,
        LabRequestPriority.Urgent => 45,
        _ => 60,
    };
}
