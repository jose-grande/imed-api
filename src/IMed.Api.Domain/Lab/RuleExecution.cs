using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Domain.Lab;

/// <summary>
/// Ejecución de una regla sobre un resultado (Épica LIS-5). Auditoría de qué regla
/// corrió sobre qué `Observation` y su verdict.
/// </summary>
public sealed class RuleExecution
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string ObservationFhirId { get; private set; } = string.Empty;
    public Guid LabRuleId { get; private set; }
    public int RuleVersion { get; private set; }
    public bool Fired { get; private set; }
    public RuleAction Verdict { get; private set; }
    public DateTimeOffset EvaluatedAt { get; private set; }

    private RuleExecution() { }

    public static RuleExecution Create(
        Guid organizationId, string observationFhirId, Guid labRuleId, int ruleVersion,
        bool fired, RuleAction verdict, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new RuleExecution
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            ObservationFhirId = observationFhirId,
            LabRuleId = labRuleId,
            RuleVersion = ruleVersion,
            Fired = fired,
            Verdict = verdict,
            EvaluatedAt = now,
        };
    }
}
