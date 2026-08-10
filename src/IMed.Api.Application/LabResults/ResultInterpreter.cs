using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabResults;

/// <summary>Flag de interpretación + rango aplicado (Épica LIS-4, L4-D2). <see cref="AppliesToSex"/>/<see cref="AppliesToAgeMin"/>/<see cref="AppliesToAgeMax"/> = población del rango elegido (R2: Observation.referenceRange.appliesTo/age).</summary>
public sealed record InterpretationResult(
    string? Code,
    decimal? Low,
    decimal? High,
    ReferenceSex? AppliesToSex = null,
    int? AppliesToAgeMin = null,
    int? AppliesToAgeMax = null);

/// <summary>
/// Calcula el flag de un resultado numérico contra los rangos de referencia
/// (LIS-1): N (normal), H/L (alto/bajo), HH/LL (crítico alto/bajo). Selecciona el
/// rango aplicable por sexo y edad. Lógica pura — sin I/O.
/// </summary>
public static class ResultInterpreter
{
    /// <summary>
    /// Elige el rango aplicable a un paciente concreto (sexo/edad), sin evaluar ningún valor.
    ///
    /// <para>Lo usa la pantalla de captura para <b>mostrar el rango mientras se digita</b>, con
    /// la misma regla de selección que después aplicará <see cref="Interpret"/> al calcular el
    /// flag: si divergieran, el técnico vería un rango y el sistema decidiría con otro.</para>
    /// </summary>
    public static ReferenceRange? SelectRange(
        IReadOnlyList<ReferenceRange> ranges, ReferenceSex sex, int? ageYears) =>
        ranges
            .Where(r => SexMatches(r.Sex, sex) && AgeMatches(r, ageYears))
            // Preferir rango sexo-específico y con bornes de edad (más específico).
            .OrderByDescending(r => r.Sex != ReferenceSex.Any && r.Sex == sex)
            .ThenByDescending(r => r.AgeMinYears != null || r.AgeMaxYears != null)
            .FirstOrDefault();

    public static InterpretationResult Interpret(
        IReadOnlyList<ReferenceRange> ranges, ReferenceSex sex, int? ageYears, decimal value)
    {
        var match = SelectRange(ranges, sex, ageYears);

        if (match is null)
        {
            return new InterpretationResult(null, null, null);
        }

        string code = "N";
        if (match.CriticalLow is { } cl && value < cl)
        {
            code = "LL";
        }
        else if (match.CriticalHigh is { } ch && value > ch)
        {
            code = "HH";
        }
        else if (match.Low is { } lo && value < lo)
        {
            code = "L";
        }
        else if (match.High is { } hi && value > hi)
        {
            code = "H";
        }
        return new InterpretationResult(
            code, match.Low, match.High,
            AppliesToSex: match.Sex == ReferenceSex.Any ? null : match.Sex,
            AppliesToAgeMin: match.AgeMinYears,
            AppliesToAgeMax: match.AgeMaxYears);
    }

    public static ReferenceSex ParseSex(string? gender) => gender?.Trim().ToLowerInvariant() switch
    {
        "male" => ReferenceSex.Male,
        "female" => ReferenceSex.Female,
        _ => ReferenceSex.Any,
    };

    public static int? AgeYearsFromBirthDate(string? birthDate, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(birthDate) || !DateOnly.TryParse(birthDate, out var bd))
        {
            return null;
        }
        var age = today.Year - bd.Year;
        if (bd > today.AddYears(-age))
        {
            age--;
        }
        return age < 0 ? null : age;
    }

    private static bool SexMatches(ReferenceSex rangeSex, ReferenceSex patientSex) =>
        rangeSex == ReferenceSex.Any || rangeSex == patientSex;

    private static bool AgeMatches(ReferenceRange r, int? ageYears)
    {
        if (ageYears is null)
        {
            return true; // edad desconocida → no restringe
        }
        if (r.AgeMinYears is { } min && ageYears < min)
        {
            return false;
        }
        if (r.AgeMaxYears is { } max && ageYears > max)
        {
            return false;
        }
        return true;
    }
}
