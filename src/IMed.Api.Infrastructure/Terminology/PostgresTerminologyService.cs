using IMed.Api.Application.Abstractions.Terminology;

namespace IMed.Api.Infrastructure.Terminology;

/// <summary>
/// Adapter del port <see cref="ITerminologyService"/> que sirve catálogos
/// almacenados en tablas Postgres locales. Rutea cada operación al
/// <see cref="INamedTerminologyCatalogReader"/> correspondiente al system URI.
///
/// Cuando se incorporen otros providers (HAPI Terminology Server, Snowstorm,
/// ElasticSearch) se reemplaza este adapter por el específico, o se introduce
/// un router multi-provider que combine ambos. El contrato del port no cambia.
/// </summary>
internal sealed class PostgresTerminologyService : ITerminologyService
{
    private const string ProviderNameValue = "Postgres";

    private readonly Dictionary<string, INamedTerminologyCatalogReader> _readers;

    public PostgresTerminologyService(IEnumerable<INamedTerminologyCatalogReader> readers)
    {
        _readers = readers.ToDictionary(r => r.System, StringComparer.Ordinal);
    }

    public Task<TerminologySearchResult> SearchAsync(
        TerminologySearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var reader = ResolveReader(request.System);
        return reader.SearchAsync(request, cancellationToken);
    }

    public Task<TerminologyConcept?> LookupAsync(
        string system,
        string code,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        var reader = ResolveReader(system);
        return reader.LookupAsync(code, language, cancellationToken);
    }

    public Task<bool> ValidateCodeAsync(
        string system,
        string code,
        CancellationToken cancellationToken = default)
    {
        var reader = ResolveReader(system);
        return reader.ValidateCodeAsync(code, cancellationToken);
    }

    /// <summary>Nombre del adapter — se propaga a <c>TerminologySearchResult.ProviderName</c>.</summary>
    public static string ProviderName => ProviderNameValue;

    private INamedTerminologyCatalogReader ResolveReader(string system)
    {
        if (!_readers.TryGetValue(system, out var reader))
        {
            throw new UnsupportedCodeSystemException(system);
        }
        return reader;
    }
}
