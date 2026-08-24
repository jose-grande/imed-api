using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabRules;

public sealed record LabRuleDto(
    Guid Id, Guid? OrganizationId, string Code, string Name, string Scope, string? TargetLoinc,
    string Action, string Severity, string? Message, bool Active)
{
    public static LabRuleDto FromDomain(LabRule r) => new(
        r.Id, r.OrganizationId, r.Code, r.Name, r.Scope.ToString(), r.TargetLoinc,
        r.Action.ToString(), r.Severity.ToString(), r.Message, r.Active);
}

public sealed record ListLabRulesQuery(Guid? OrganizationId);

public sealed class ListLabRulesHandler(ILabRuleRepository repo)
{
    public async Task<IReadOnlyList<LabRuleDto>> HandleAsync(ListLabRulesQuery query, CancellationToken ct = default)
    {
        var rows = await repo.ListRulesAsync(query.OrganizationId, ct);
        return rows.Select(LabRuleDto.FromDomain).ToList();
    }
}

public sealed record CreateLabRuleCommand(
    Guid? OrganizationId, string Code, string Name, string Scope, string? TargetLoinc,
    string Action, string Severity, string? Message, string LogicJson);

public sealed class CreateLabRuleHandler(ILabRuleRepository repo, TimeProvider timeProvider)
{
    public async Task<LabRuleDto> HandleAsync(CreateLabRuleCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.LogicJson))
        {
            throw new ArgumentException("LogicJson es obligatorio.", nameof(cmd));
        }
        if (await repo.RuleCodeExistsAsync(cmd.OrganizationId, cmd.Code, ct))
        {
            throw new InvalidOperationException($"Ya existe una regla con code='{cmd.Code}'.");
        }
        var scope = ParseEnum<RuleScope>(cmd.Scope, nameof(RuleScope));
        var action = ParseEnum<RuleAction>(cmd.Action, nameof(RuleAction));
        var severity = ParseEnum<RuleSeverity>(cmd.Severity, nameof(RuleSeverity));

        var rule = LabRule.Create(cmd.Code, cmd.Name, scope, action, cmd.TargetLoinc, severity, cmd.Message,
            cmd.OrganizationId, active: true, timeProvider: timeProvider);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var version = LabRuleVersion.Create(rule.Id, 1, cmd.LogicJson, today, RuleVersionStatus.Active, timeProvider);
        await repo.AddRuleWithVersionAsync(rule, version, ct);
        return LabRuleDto.FromDomain(rule);
    }

    private static TEnum ParseEnum<TEnum>(string value, string name) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException($"Valor inválido para {name}: '{value}'.");
        }
        return parsed;
    }
}
