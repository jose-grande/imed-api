using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Identifiers;

/// <summary>
/// Adapter del registry que lee la lista de <c>appsettings:IdentifierTypes:Types</c>.
/// Singleton — la config es estática durante el lifetime del proceso; cambios
/// requieren restart.
/// </summary>
public sealed class ConfigBasedIdentifierTypeRegistry : IIdentifierTypeRegistry
{
    private readonly IReadOnlyList<IdentifierType> _all;
    private readonly Dictionary<string, IReadOnlyList<IdentifierType>> _byCountry;
    private readonly Dictionary<(string Country, string Code), IdentifierType> _byKey;

    public ConfigBasedIdentifierTypeRegistry(IOptions<IdentifierTypesOptions> options)
    {
        _all = options.Value.Types
            .Select(e => new IdentifierType(
                CountryCode: e.CountryCode,
                Code: e.Code,
                Label: e.Label,
                FhirSystemUri: e.FhirSystemUri,
                Regex: e.Regex,
                Hl7V2Code: e.Hl7V2Code,
                NormalizationHint: e.NormalizationHint))
            .ToList();

        _byCountry = _all
            .GroupBy(t => t.CountryCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<IdentifierType>)g.ToList(),
                StringComparer.OrdinalIgnoreCase);

        _byKey = _all.ToDictionary(
            t => (t.CountryCode, t.Code),
            t => t);
    }

    public IReadOnlyList<IdentifierType> All => _all;

    public IReadOnlyList<IdentifierType> ForCountry(string countryCode)
    {
        return _byCountry.TryGetValue(countryCode, out var list)
            ? list
            : Array.Empty<IdentifierType>();
    }

    public IdentifierType? Find(string countryCode, string code)
    {
        return _byKey.TryGetValue((countryCode, code), out var type) ? type : null;
    }
}
