using System.Globalization;

namespace IMed.Api.Application.Patients;

/// <summary>
/// Edad legible de un paciente para documentos impresos (receta).
///
/// <para><b>Por qué no solo años.</b> En pediatría la dosis depende de la edad, y "0 años" no le
/// sirve a nadie: un lactante se lee en meses y un recién nacido en días.</para>
/// </summary>
public static class PatientAge
{
    /// <summary>
    /// "35 años", "1 año", "8 meses", "1 mes", "12 días". Null si la fecha de nacimiento falta, no
    /// se entiende o es posterior a <paramref name="onDate"/>. Acepta las precisiones de FHIR
    /// (<c>yyyy</c>, <c>yyyy-MM</c>, <c>yyyy-MM-dd</c>); con una fecha parcial solo se dan años.
    /// </summary>
    public static string? Format(string? birthDate, DateOnly onDate)
    {
        if (string.IsNullOrWhiteSpace(birthDate))
        {
            return null;
        }
        var raw = birthDate.Trim();

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var bd))
        {
            if (bd > onDate)
            {
                return null;
            }
            var years = onDate.Year - bd.Year - (bd.AddYears(onDate.Year - bd.Year) > onDate ? 1 : 0);
            if (years >= 1)
            {
                return years == 1 ? "1 año" : $"{years} años";
            }
            var months = (onDate.Year - bd.Year) * 12 + onDate.Month - bd.Month - (onDate.Day < bd.Day ? 1 : 0);
            if (months >= 1)
            {
                return months == 1 ? "1 mes" : $"{months} meses";
            }
            var days = onDate.DayNumber - bd.DayNumber;
            return days == 1 ? "1 día" : $"{days} días";
        }

        // Precisión parcial (solo año, o año y mes): años completos si se pueden asegurar.
        if (DateOnly.TryParseExact(raw, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ym)
            || DateOnly.TryParseExact(raw, "yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out ym))
        {
            if (ym > onDate)
            {
                return null;
            }
            var years = onDate.Year - ym.Year - (raw.Length > 4 && onDate.Month < ym.Month ? 1 : 0);
            return years < 1 ? null : years == 1 ? "1 año" : $"{years} años";
        }

        return null;
    }
}
