using FluentAssertions;
using IMed.Api.Application.LabRules;
using Xunit;

namespace IMed.Api.IntegrationTests.LabRules;

/// <summary>Tests unitarios del evaluador JSON Logic (Épica LIS-5, L5-F1). Lógica pura.</summary>
public sealed class JsonLogicEvaluatorTests
{
    private static Dictionary<string, object?> Ctx(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("{\">\":[{\"var\":\"value\"},125]}", 140.0, true)]
    [InlineData("{\">\":[{\"var\":\"value\"},125]}", 100.0, false)]
    [InlineData("{\"<\":[{\"var\":\"value\"},40]}", 30.0, true)]
    [InlineData("{\"<=\":[{\"var\":\"value\"},40]}", 40.0, true)]
    [InlineData("{\">=\":[{\"var\":\"value\"},40]}", 39.0, false)]
    public void Evaluate_comparacionesNumericas(string logic, double value, bool expected)
    {
        JsonLogicEvaluator.Evaluate(logic, Ctx(("value", value))).Should().Be(expected);
    }

    [Fact]
    public void Evaluate_and_or_not()
    {
        var ctx = Ctx(("value", 7.2), ("isCritical", true));
        JsonLogicEvaluator.Evaluate("{\"and\":[{\">\":[{\"var\":\"value\"},6.5]},{\"var\":\"isCritical\"}]}", ctx).Should().BeTrue();
        JsonLogicEvaluator.Evaluate("{\"or\":[{\"<\":[{\"var\":\"value\"},2]},{\"var\":\"isCritical\"}]}", ctx).Should().BeTrue();
        JsonLogicEvaluator.Evaluate("{\"!\":[{\"var\":\"isCritical\"}]}", ctx).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_deltaCheck_porcentaje()
    {
        // regla: deltaPct > 20  → revisión
        var logic = "{\">\":[{\"var\":\"deltaPct\"},20]}";
        JsonLogicEvaluator.Evaluate(logic, Ctx(("deltaPct", 50.0))).Should().BeTrue();
        JsonLogicEvaluator.Evaluate(logic, Ctx(("deltaPct", 10.0))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_abs_y_aritmetica()
    {
        JsonLogicEvaluator.Evaluate("{\">\":[{\"abs\":[{\"var\":\"d\"}]},20]}", Ctx(("d", -30.0))).Should().BeTrue();
        JsonLogicEvaluator.Evaluate("{\"==\":[{\"+\":[{\"var\":\"a\"},{\"var\":\"b\"}]},5]}", Ctx(("a", 2.0), ("b", 3.0))).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_eq_conStringYBool()
    {
        JsonLogicEvaluator.Evaluate("{\"==\":[{\"var\":\"flag\"},\"HH\"]}", Ctx(("flag", "HH"))).Should().BeTrue();
        JsonLogicEvaluator.Evaluate("{\"==\":[{\"var\":\"flag\"},\"HH\"]}", Ctx(("flag", "N"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_in()
    {
        JsonLogicEvaluator.Evaluate("{\"in\":[{\"var\":\"flag\"},[\"HH\",\"LL\"]]}", Ctx(("flag", "LL"))).Should().BeTrue();
        JsonLogicEvaluator.Evaluate("{\"in\":[{\"var\":\"flag\"},[\"HH\",\"LL\"]]}", Ctx(("flag", "H"))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_varAusente_usaDefaultOFalse()
    {
        JsonLogicEvaluator.Evaluate("{\"var\":\"nope\"}", Ctx()).Should().BeFalse();
        JsonLogicEvaluator.Evaluate("{\">\":[{\"var\":[\"nope\",100]},50]}", Ctx()).Should().BeTrue();
    }

    [Fact]
    public void Evaluate_jsonInvalido_devuelveFalse()
    {
        JsonLogicEvaluator.Evaluate("{not valid json", Ctx(("value", 1.0))).Should().BeFalse();
    }

    [Fact]
    public void Evaluate_comparacionConNull_esFalse()
    {
        // value ausente (null) → ninguna comparación numérica debe dispararse.
        JsonLogicEvaluator.Evaluate("{\">\":[{\"var\":\"value\"},5]}", Ctx(("value", null))).Should().BeFalse();
    }
}
