using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabDashboard;

public sealed record PriorityBreakdownDto(int Stat, int Urgent, int Asap, int Routine);

public sealed record LabDashboardDto(
    int ReceivedToday,
    int InLab,
    int AwaitingValidation,
    int CriticalToday,
    int ReleasedToday,
    int Blocked,
    int? TatAverageMinutes,
    int TatBreaches,
    PriorityBreakdownDto Priority);

public sealed record GetLabDashboardQuery(Guid OrganizationId);

/// <summary>
/// Métricas operacionales del laboratorio (Épica LIS-8). Agrega `order_intake`
/// (Postgres) + críticos (`rule_execution`) + esperando validación (HAPI).
/// </summary>
public sealed class GetLabDashboardHandler(
    ILabOperationsRepository ops,
    ILabRuleRepository rules,
    IHapiFhirClient hapi,
    TimeProvider timeProvider)
{
    public async Task<LabDashboardDto> HandleAsync(GetLabDashboardQuery query, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var today = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var intakes = await ops.ListInHouseIntakesAsync(query.OrganizationId, null, ct);

        var receivedToday = intakes.Count(i => i.ReceivedAt is { } r && r >= today);
        var inLab = intakes.Count(i => i.Status == OrderIntakeStatus.Received);
        var releasedToday = intakes.Count(i => i.Status == OrderIntakeStatus.Completed && i.UpdatedAt >= today);
        var blocked = intakes.Count(i => i.Status == OrderIntakeStatus.Blocked);

        var inLabIntakes = intakes.Where(i => i.Status == OrderIntakeStatus.Received).ToList();
        var priority = new PriorityBreakdownDto(
            Stat: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Stat),
            Urgent: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Urgent),
            Asap: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Asap),
            Routine: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Routine));

        // TAT de las completadas: received_at → liberación (updated_at).
        var tats = intakes
            .Where(i => i.Status == OrderIntakeStatus.Completed && i.ReceivedAt is not null)
            .Select(i => (Minutes: (int)Math.Max(0, (i.UpdatedAt - i.ReceivedAt!.Value).TotalMinutes), i.Priority))
            .ToList();
        int? tatAvg = tats.Count > 0 ? (int)tats.Average(t => t.Minutes) : null;
        var tatBreaches = tats.Count(t => t.Minutes > DefaultTargetMinutes(t.Priority));

        var awaiting = (await hapi.SearchPreliminaryReportsAsync($"org-{query.OrganizationId}", ct)).Count;
        var critical = await rules.CountCriticalExecutionsAsync(query.OrganizationId, today, ct);

        return new LabDashboardDto(
            receivedToday, inLab, awaiting, critical, releasedToday, blocked, tatAvg, tatBreaches, priority);
    }

    private static int DefaultTargetMinutes(LabRequestPriority priority) => priority switch
    {
        LabRequestPriority.Stat => 30,
        LabRequestPriority.Asap => 45,
        LabRequestPriority.Urgent => 45,
        _ => 60,
    };
}
