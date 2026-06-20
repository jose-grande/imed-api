using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabRules;

/// <summary>Entrada para evaluar un resultado (Épica LIS-5, L5-D3).</summary>
public sealed record ResultEvaluationInput(
    Guid OrganizationId,
    string? Loinc,
    decimal? Value,
    string? Flag,
    string Sex,
    int? AgeYears,
    decimal? PriorValue);

/// <summary>Una regla evaluada sobre el resultado.</summary>
public sealed record EvaluatedRule(
    Guid RuleId, int Version, RuleAction Action, bool Fired, string Name, RuleSeverity Severity, string? Message);

/// <summary>Verdict del motor sobre un resultado.</summary>
public sealed record ResultEvaluation(
    bool AutoValidated,
    bool RequiresReview,
    bool IsCritical,
    decimal? DeltaPct,
    IReadOnlyList<EvaluatedRule> Rules);

public interface IResultEvaluationService
{
    Task<ResultEvaluation> EvaluateAsync(ResultEvaluationInput input, CancellationToken cancellationToken = default);
}

/// <summary>
/// Motor de reglas (LIS-5): arma el contexto (incluye delta vs histórico), corre
/// las reglas activas (JSON Logic) y decide auto-validación / revisión / crítico
/// (L5-D4). Lógica determinística — el delta lo provee el caller (PriorValue).
/// </summary>
public sealed class ResultEvaluationService(ILabRuleRepository rules) : IResultEvaluationService
{
    public async Task<ResultEvaluation> EvaluateAsync(ResultEvaluationInput input, CancellationToken cancellationToken = default)
    {
        decimal? deltaPct = null;
        if (input.Value is { } v && input.PriorValue is { } prior && prior != 0)
        {
            deltaPct = Math.Abs((v - prior) / prior) * 100m;
        }

        var flag = input.Flag;
        var isCritical = flag is "HH" or "LL";

        var context = new Dictionary<string, object?>
        {
            ["value"] = input.Value is { } val ? (double)val : null,
            ["flag"] = flag,
            ["isCritical"] = isCritical,
            ["deltaPct"] = deltaPct is { } d ? (double)d : null,
            ["hasPrior"] = input.PriorValue is not null,
            ["priorValue"] = input.PriorValue is { } pv ? (double)pv : null,
            ["ageYears"] = input.AgeYears,
            ["sex"] = input.Sex,
        };

        var active = await rules.ListActiveRulesAsync(input.OrganizationId, input.Loinc, cancellationToken);
        var evaluated = new List<EvaluatedRule>();
        var requiresReview = false;
        var anyAutoValidate = false;

        foreach (var rule in active)
        {
            var fired = JsonLogicEvaluator.Evaluate(rule.LogicJson, context);
            evaluated.Add(new EvaluatedRule(rule.RuleId, rule.Version, rule.Action, fired, rule.Name, rule.Severity, rule.Message));
            if (!fired)
            {
                continue;
            }
            switch (rule.Action)
            {
                case RuleAction.FlagCritical:
                    isCritical = true;
                    break;
                case RuleAction.RequireReview:
                    requiresReview = true;
                    break;
                case RuleAction.AutoValidate:
                    anyAutoValidate = true;
                    break;
                case RuleAction.Alert:
                default:
                    break;
            }
        }

        // Auto-validación (L5-D4): nunca libera críticos ni los marcados para revisión.
        // Por defecto solo libera resultados normales; una regla AutoValidate puede ampliar.
        var autoValidated = !isCritical && !requiresReview && (flag == "N" || anyAutoValidate);

        return new ResultEvaluation(autoValidated, requiresReview, isCritical, deltaPct, evaluated);
    }
}
