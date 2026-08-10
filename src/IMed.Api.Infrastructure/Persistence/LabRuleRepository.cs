using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Repositorio EF Core de reglas del laboratorio (Épica LIS-5).</summary>
public sealed class LabRuleRepository(AppDbContext db) : ILabRuleRepository
{
    public async Task<IReadOnlyList<ActiveLabRule>> ListActiveRulesAsync(
        Guid? organizationId, string? loinc, CancellationToken cancellationToken = default)
    {
        var rules = await db.LabRules.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId
                && r.Active
                && (r.Scope == RuleScope.Global || (loinc != null && r.TargetLoinc == loinc)))
            .ToListAsync(cancellationToken);
        if (rules.Count == 0)
        {
            return [];
        }

        var ruleIds = rules.Select(r => r.Id).ToList();
        var versions = await db.LabRuleVersions.AsNoTracking()
            .Where(v => ruleIds.Contains(v.LabRuleId) && v.Status == RuleVersionStatus.Active)
            .ToListAsync(cancellationToken);

        var activeByRule = versions
            .GroupBy(v => v.LabRuleId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Version).First());

        var result = new List<ActiveLabRule>();
        foreach (var r in rules)
        {
            if (activeByRule.TryGetValue(r.Id, out var v))
            {
                result.Add(new ActiveLabRule(
                    r.Id, r.Code, r.Name, r.Scope, r.TargetLoinc, r.Action, r.Severity, r.Message, v.Version, v.LogicJson));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<LabRule>> ListRulesAsync(Guid? organizationId, CancellationToken cancellationToken = default) =>
        await db.LabRules.AsNoTracking()
            .Where(r => r.OrganizationId == organizationId)
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);

    public Task<bool> RuleCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default)
    {
        var c = code.Trim();
        return db.LabRules.AnyAsync(r => r.OrganizationId == organizationId && r.Code == c, cancellationToken);
    }

    public async Task AddRuleWithVersionAsync(LabRule rule, LabRuleVersion version, CancellationToken cancellationToken = default)
    {
        db.LabRules.Add(rule);
        db.LabRuleVersions.Add(version);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<bool> AnyGlobalRulesAsync(CancellationToken cancellationToken = default) =>
        db.LabRules.AnyAsync(r => r.OrganizationId == null, cancellationToken);

    public async Task AddRuleExecutionsAsync(IReadOnlyList<RuleExecution> executions, CancellationToken cancellationToken = default)
    {
        if (executions.Count == 0)
        {
            return;
        }
        db.RuleExecutions.AddRange(executions);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<int> CountCriticalExecutionsAsync(Guid organizationId, DateTimeOffset sinceUtc, CancellationToken cancellationToken = default) =>
        db.RuleExecutions.CountAsync(
            x => x.OrganizationId == organizationId
                && x.Fired
                && x.Verdict == RuleAction.FlagCritical
                && x.EvaluatedAt >= sinceUtc,
            cancellationToken);
}
