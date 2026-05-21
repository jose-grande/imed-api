using System.Text.RegularExpressions;
using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Identifiers;

/// <summary>
/// Implementación de <see cref="IIdentifierValidator"/> que aplica el regex
/// declarado en el registry y normaliza el valor según la convención de cada
/// tipo (guiones para DNI/RTN, uppercase para Pasaporte / Carnet).
///
/// <para>Sin check digit por ahora — fallback registrado como follow-up
/// post-piloto (Épica H §H0). El día que tengamos el algoritmo del RNP, se
/// agrega una capa específica que corra después del regex.</para>
/// </summary>
public sealed class IdentifierValidator(IIdentifierTypeRegistry registry) : IIdentifierValidator
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    public IdentifierValidationResult Validate(string countryCode, string typeCode, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Fail("El valor del identifier no puede estar vacío.");
        }

        var type = registry.Find(countryCode, typeCode);
        if (type is null)
        {
            return Fail($"Tipo de identifier '{typeCode}' no soportado para país '{countryCode}'.");
        }

        // Trim + apply per-type pre-normalization (uppercase para alfanuméricos,
        // strip whitespace siempre). Después pasamos por el regex.
        var trimmed = value.Trim();
        var preNormalized = ApplyPreNormalization(trimmed, type);

        Regex regex;
        try
        {
            regex = new Regex(type.Regex, RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException ex)
        {
            // Regex mal armada en config — fallar ruidoso para que el dev arregle.
            return Fail($"Config inválida: regex de '{type.Code}' no compila ({ex.Message}).");
        }

        if (!regex.IsMatch(preNormalized))
        {
            var hint = string.IsNullOrWhiteSpace(type.NormalizationHint)
                ? string.Empty
                : $" Formato esperado: {type.NormalizationHint}.";
            return Fail($"'{value}' no coincide con el formato de {type.Label}.{hint}");
        }

        var normalized = NormalizeWithDashes(preNormalized, type);
        return new IdentifierValidationResult(
            Valid: true,
            Normalized: normalized,
            Errors: Array.Empty<string>());
    }

    private static IdentifierValidationResult Fail(string error) =>
        new(Valid: false, Normalized: null, Errors: new[] { error });

    private static string ApplyPreNormalization(string value, Domain.Identifiers.IdentifierType type)
    {
        // Strip whitespace interno siempre.
        var noSpaces = string.Concat(value.Where(c => !char.IsWhiteSpace(c)));

        // Pasaporte y Carnet de Residente: uppercase canónico.
        if (type.Code is "Pasaporte" or "CarnetResidente")
        {
            return noSpaces.ToUpperInvariant();
        }
        return noSpaces;
    }

    private static string NormalizeWithDashes(string value, Domain.Identifiers.IdentifierType type)
    {
        // Para DNI HN (13 dígitos) y RTN (14 dígitos), aseguramos los guiones
        // canónicos. El input puede venir con o sin guiones; si ya los tiene,
        // re-armamos sobre los dígitos puros para evitar drift.
        if (type.CountryCode != "HN")
        {
            return value;
        }

        if (type.Code == "IdentidadNacional")
        {
            var digits = string.Concat(value.Where(char.IsDigit));
            if (digits.Length == 13)
            {
                return $"{digits[..4]}-{digits[4..8]}-{digits[8..13]}";
            }
        }

        if (type.Code == "RTN")
        {
            var digits = string.Concat(value.Where(char.IsDigit));
            if (digits.Length == 14)
            {
                return $"{digits[..4]}-{digits[4..8]}-{digits[8..14]}";
            }
        }

        return value;
    }
}
