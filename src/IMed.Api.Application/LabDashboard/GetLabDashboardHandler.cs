using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
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
///
/// <para>"Hoy" es el día de la clínica (<see cref="IClinicClock"/>), no el UTC: cortar en la
/// medianoche UTC reiniciaba los contadores a las 18:00 en Honduras. El TAT es el de lo liberado
/// hoy, medido con <see cref="LabTat"/> hasta la liberación; antes promediaba todo lo liberado
/// desde siempre y hasta <c>updated_at</c>.</para>
/// </summary>
public sealed class GetLabDashboardHandler(
    ILabOperationsRepository ops,
    ILabRuleRepository rules,
    IHapiFhirClient hapi,
    IClinicClock clinicClock,
    TimeProvider timeProvider)
{
    public async Task<LabDashboardDto> HandleAsync(GetLabDashboardQuery query, CancellationToken ct = default)
    {
        var now = timeProvider.GetUtcNow();
        var today = clinicClock.StartOf(clinicClock.DateOf(now));

        var intakes = await ops.ListInHouseIntakesAsync(query.OrganizationId, null, ct);

        var receivedToday = intakes.Count(i => i.ReceivedAt is { } r && r >= today);
        var inLab = intakes.Count(i => i.Status == OrderIntakeStatus.Received);
        var released = intakes.Where(i => LabTat.ReleasedAt(i) is { } at && at >= today).ToList();
        var releasedToday = released.Count;
        var blocked = intakes.Count(i => i.Status == OrderIntakeStatus.Blocked);

        var inLabIntakes = intakes.Where(i => i.Status == OrderIntakeStatus.Received).ToList();
        var priority = new PriorityBreakdownDto(
            Stat: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Stat),
            Urgent: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Urgent),
            Asap: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Asap),
            Routine: inLabIntakes.Count(i => i.Priority == LabRequestPriority.Routine));

        // TAT de lo liberado hoy: recepción → liberación.
        var tats = released
            .Select(i => (Minutes: LabTat.Minutes(i, now), i.Priority))
            .Where(t => t.Minutes is not null)
            .Select(t => (Minutes: t.Minutes!.Value, t.Priority))
            .ToList();
        int? tatAvg = tats.Count > 0 ? (int)Math.Round(tats.Average(t => t.Minutes)) : null;
        var tatBreaches = tats.Count(t => t.Minutes > LabTat.DefaultTargetMinutes(t.Priority));

        var awaiting = (await hapi.SearchPreliminaryReportsAsync($"org-{query.OrganizationId}", ct)).Count;
        var critical = await rules.CountCriticalExecutionsAsync(query.OrganizationId, today, ct);

        return new LabDashboardDto(
            receivedToday, inLab, awaiting, critical, releasedToday, blocked, tatAvg, tatBreaches, priority);
    }
}
