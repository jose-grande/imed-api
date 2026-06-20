using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.Abstractions;

/// <summary>Regla activa con su versión vigente (para el motor — LIS-5).</summary>
public sealed record ActiveLabRule(
    Guid RuleId,
    string Code,
    string Name,
    RuleScope Scope,
    string? TargetLoinc,
    RuleAction Action,
    RuleSeverity Severity,
    string? Message,
    int Version,
    string LogicJson);

public interface ILabRuleRepository
{
    /// <summary>Reglas activas (global + por LOINC) con su versión Active, para evaluar un resultado.</summary>
    Task<IReadOnlyList<ActiveLabRule>> ListActiveRulesAsync(Guid? organizationId, string? loinc, CancellationToken cancellationToken = default);

    /// <summary>Listado admin de reglas (global + org).</summary>
    Task<IReadOnlyList<LabRule>> ListRulesAsync(Guid? organizationId, CancellationToken cancellationToken = default);

    Task<bool> RuleCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default);

    /// <summary>Crea una regla + su primera versión (atómico).</summary>
    Task AddRuleWithVersionAsync(LabRule rule, LabRuleVersion version, CancellationToken cancellationToken = default);

    /// <summary>¿Hay reglas globales? (idempotencia del seeder).</summary>
    Task<bool> AnyGlobalRulesAsync(CancellationToken cancellationToken = default);

    Task AddRuleExecutionsAsync(IReadOnlyList<RuleExecution> executions, CancellationToken cancellationToken = default);

    /// <summary>Cuenta ejecuciones que dispararon acción crítica desde una fecha (dashboard LIS-8).</summary>
    Task<int> CountCriticalExecutionsAsync(Guid organizationId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default);
}
