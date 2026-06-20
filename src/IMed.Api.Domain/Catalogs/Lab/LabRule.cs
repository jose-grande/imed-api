namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Regla clínica del laboratorio (Épica LIS-5). La condición (JSON Logic) vive en
/// la <see cref="LabRuleVersion"/> activa; el `LabRule` lleva la acción/severidad
/// y el alcance (global o por prueba LOINC). <see cref="OrganizationId"/> null =
/// plantilla global (L1-D1).
/// </summary>
public sealed class LabRule
{
    public Guid Id { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public RuleScope Scope { get; private set; }
    /// <summary>LOINC al que aplica si Scope=Test; null si Global.</summary>
    public string? TargetLoinc { get; private set; }
    public RuleAction Action { get; private set; }
    public RuleSeverity Severity { get; private set; } = RuleSeverity.Warning;
    public string? Message { get; private set; }
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabRule() { }

    public static LabRule Create(
        string code, string name, RuleScope scope, RuleAction action,
        string? targetLoinc = null, RuleSeverity severity = RuleSeverity.Warning, string? message = null,
        Guid? organizationId = null, bool active = true, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        if (scope == RuleScope.Test && string.IsNullOrWhiteSpace(targetLoinc))
        {
            throw new ArgumentException("TargetLoinc es obligatorio para reglas por prueba.", nameof(targetLoinc));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabRule
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            Scope = scope,
            TargetLoinc = string.IsNullOrWhiteSpace(targetLoinc) ? null : targetLoinc.Trim(),
            Action = action,
            Severity = severity,
            Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(string name, RuleAction action, RuleSeverity severity, string? message, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        Action = action;
        Severity = severity;
        Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim();
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}

/// <summary>Versión (versionada/auditable) de una regla: su condición JSON Logic.</summary>
public sealed class LabRuleVersion
{
    public Guid Id { get; private set; }
    public Guid LabRuleId { get; private set; }
    public int Version { get; private set; }
    public string LogicJson { get; private set; } = "{}";
    public RuleVersionStatus Status { get; private set; } = RuleVersionStatus.Active;
    public DateOnly EffectiveDate { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private LabRuleVersion() { }

    public static LabRuleVersion Create(
        Guid labRuleId, int version, string logicJson, DateOnly effectiveDate,
        RuleVersionStatus status = RuleVersionStatus.Active, TimeProvider? timeProvider = null)
    {
        if (labRuleId == Guid.Empty)
        {
            throw new ArgumentException("LabRuleId es obligatorio.", nameof(labRuleId));
        }
        if (string.IsNullOrWhiteSpace(logicJson))
        {
            throw new ArgumentException("LogicJson es obligatorio.", nameof(logicJson));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabRuleVersion
        {
            Id = Guid.CreateVersion7(now),
            LabRuleId = labRuleId,
            Version = version,
            LogicJson = logicJson,
            Status = status,
            EffectiveDate = effectiveDate,
            CreatedAt = now,
        };
    }

    public void SetStatus(RuleVersionStatus status) => Status = status;
}
