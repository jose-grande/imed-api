using IMed.Api.Application.Abstractions.Terminology;

namespace IMed.Api.Infrastructure.Terminology;

/// <summary>
/// Reader interno del adapter Postgres. Cada CodeSystem soportado tiene su
/// propio reader (Icd11CatalogReader, LoincCatalogReader, ...) que conoce
/// el shape de su tabla y mapea filas a <see cref="TerminologyConcept"/>.
///
/// NO es port — es un detalle interno del <see cref="PostgresTerminologyService"/>.
/// El día que migremos a HAPI/Snowstorm, los readers desaparecen junto con
/// las tablas Postgres del catálogo.
/// </summary>
internal interface INamedTerminologyCatalogReader
{
    /// <summary>URI canónico del CodeSystem que este reader maneja.</summary>
    string System { get; }

    Task<TerminologySearchResult> SearchAsync(
        TerminologySearchRequest request,
        CancellationToken cancellationToken);

    Task<TerminologyConcept?> LookupAsync(
        string code,
        string? language,
        CancellationToken cancellationToken);

    Task<bool> ValidateCodeAsync(string code, CancellationToken cancellationToken);
}
