using IMed.Api.Domain.Identifiers;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Registry de tipos de identifier. Implementación config-driven en
/// <c>Infrastructure</c> bindea desde <c>appsettings.json</c>; permite agregar
/// países nuevos sin redeploy de código.
/// </summary>
public interface IIdentifierTypeRegistry
{
    /// <summary>Todos los tipos registrados (uso interno, ej. validación cross-country).</summary>
    IReadOnlyList<IdentifierType> All { get; }

    /// <summary>Tipos disponibles para el país indicado. Devuelve lista vacía si no hay match.</summary>
    IReadOnlyList<IdentifierType> ForCountry(string countryCode);

    /// <summary>
    /// Lookup por código + país. Devuelve <c>null</c> si no hay match — el caller
    /// decide si es 400 ("tipo desconocido") o un default.
    /// </summary>
    IdentifierType? Find(string countryCode, string code);
}
