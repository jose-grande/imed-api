using FluentAssertions;
using IMed.Api.Application.LabResults;
using IMed.Api.Domain.Catalogs.Lab;
using Xunit;

namespace IMed.Api.IntegrationTests.LabResults;

/// <summary>Tests unitarios del cálculo de flags (Épica LIS-4). Lógica pura — sin fixture.</summary>
public sealed class ResultInterpreterTests
{
    private static ReferenceRange Range(
        ReferenceSex sex, decimal? low, decimal? high, decimal? cLow, decimal? cHigh,
        int? ageMin = null, int? ageMax = null) =>
        ReferenceRange.Create(Guid.NewGuid(), sex, ageMin, ageMax, null, null, low, high, cLow, cHigh, "mmol/L");

    [Theory]
    [InlineData(4.0, "N")]
    [InlineData(3.0, "L")]
    [InlineData(6.0, "H")]
    [InlineData(2.0, "LL")]
    [InlineData(7.0, "HH")]
    public void Interpret_potasio_devuelveFlagCorrecto(double value, string expected)
    {
        var ranges = new[] { Range(ReferenceSex.Any, 3.5m, 5.2m, 2.5m, 6.5m) };
        var result = ResultInterpreter.Interpret(ranges, ReferenceSex.Any, 40, (decimal)value);
        result.Code.Should().Be(expected);
        result.Low.Should().Be(3.5m);
        result.High.Should().Be(5.2m);
    }

    [Fact]
    public void Interpret_eligeRangoPorSexo()
    {
        var ranges = new[]
        {
            Range(ReferenceSex.Male, 13.5m, 17.5m, 6m, 20m),
            Range(ReferenceSex.Female, 12.0m, 15.5m, 6m, 20m),
        };
        // 13.0 es bajo para hombre pero normal para mujer.
        ResultInterpreter.Interpret(ranges, ReferenceSex.Male, 30, 13.0m).Code.Should().Be("L");
        ResultInterpreter.Interpret(ranges, ReferenceSex.Female, 30, 13.0m).Code.Should().Be("N");
    }

    [Fact]
    public void Interpret_exponePoblacionDelRango_paraAppliesToYAge()
    {
        // R2: el rango elegido alimenta Observation.referenceRange.appliesTo (sexo) + age (años).
        var ranges = new[] { Range(ReferenceSex.Male, 13.5m, 17.5m, 6m, 20m, ageMin: 18, ageMax: 65) };
        var result = ResultInterpreter.Interpret(ranges, ReferenceSex.Male, 40, 15m);
        result.AppliesToSex.Should().Be(ReferenceSex.Male);
        result.AppliesToAgeMin.Should().Be(18);
        result.AppliesToAgeMax.Should().Be(65);
    }

    [Fact]
    public void Interpret_rangoAny_noExponeAppliesToSex()
    {
        // Rango sexo-indistinto: no se emite appliesTo de sexo (sería ruido).
        var ranges = new[] { Range(ReferenceSex.Any, 3.5m, 5.2m, null, null) };
        var result = ResultInterpreter.Interpret(ranges, ReferenceSex.Female, 30, 4m);
        result.AppliesToSex.Should().BeNull();
    }

    [Fact]
    public void Interpret_sinRangoAplicable_devuelveNull()
    {
        var ranges = new[] { Range(ReferenceSex.Male, 1m, 2m, null, null) };
        var result = ResultInterpreter.Interpret(ranges, ReferenceSex.Female, 30, 1.5m);
        result.Code.Should().BeNull();
    }

    [Fact]
    public void Interpret_respetaBornesDeEdad()
    {
        var ranges = new[]
        {
            Range(ReferenceSex.Any, 60m, 100m, null, null, ageMin: 0, ageMax: 17),  // niños
            Range(ReferenceSex.Any, 70m, 99m, 40m, 500m, ageMin: 18),               // adultos
        };
        // adulto: usa el rango 70-99
        ResultInterpreter.Interpret(ranges, ReferenceSex.Any, 40, 65m).Code.Should().Be("L");
        // niño: usa el rango 60-100
        ResultInterpreter.Interpret(ranges, ReferenceSex.Any, 10, 65m).Code.Should().Be("N");
    }

    /// <summary>
    /// La pantalla de captura muestra el rango con <c>SelectRange</c> y el sistema calcula el
    /// flag con <c>Interpret</c>. Si eligieran rangos distintos, el técnico vería un límite y
    /// el sistema decidiría con otro — por eso comparten la misma selección.
    /// </summary>
    [Fact]
    public void SelectRange_eligeElMismoRangoQueUsaInterpret()
    {
        var ranges = new[]
        {
            Range(ReferenceSex.Male, 13.5m, 17.5m, 6m, 20m),
            Range(ReferenceSex.Female, 12.0m, 15.5m, 6m, 20m),
            Range(ReferenceSex.Any, 11.0m, 18.0m, 6m, 20m),
        };

        foreach (var sex in new[] { ReferenceSex.Male, ReferenceSex.Female, ReferenceSex.Any })
        {
            var selected = ResultInterpreter.SelectRange(ranges, sex, 40);
            var interpreted = ResultInterpreter.Interpret(ranges, sex, 40, 14m);

            selected.Should().NotBeNull();
            selected!.Low.Should().Be(interpreted.Low);
            selected.High.Should().Be(interpreted.High);
        }
    }

    [Fact]
    public void SelectRange_sinRangoAplicable_devuelveNull()
    {
        // Rango solo pediátrico: un adulto no matchea y la captura debe mostrar "sin rango".
        var ranges = new[] { Range(ReferenceSex.Any, 60m, 100m, 40m, 200m, ageMin: 0, ageMax: 12) };
        ResultInterpreter.SelectRange(ranges, ReferenceSex.Any, 40).Should().BeNull();
    }

    [Theory]
    [InlineData("male", ReferenceSex.Male)]
    [InlineData("female", ReferenceSex.Female)]
    [InlineData("other", ReferenceSex.Any)]
    [InlineData(null, ReferenceSex.Any)]
    public void ParseSex_mapea(string? gender, ReferenceSex expected)
    {
        ResultInterpreter.ParseSex(gender).Should().Be(expected);
    }

    [Fact]
    public void AgeYearsFromBirthDate_calcula()
    {
        var today = new DateOnly(2026, 6, 20);
        ResultInterpreter.AgeYearsFromBirthDate("1990-01-15", today).Should().Be(36);
        ResultInterpreter.AgeYearsFromBirthDate("2026-01-10", today).Should().Be(0); // bebé de meses
        ResultInterpreter.AgeYearsFromBirthDate("2025-09-01", today).Should().Be(0); // aún no cumple 1
        ResultInterpreter.AgeYearsFromBirthDate(null, today).Should().BeNull();
        ResultInterpreter.AgeYearsFromBirthDate("2026-12-31", today).Should().BeNull(); // fecha futura → inválida
    }
}
