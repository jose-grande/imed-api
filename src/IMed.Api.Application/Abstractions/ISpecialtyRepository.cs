using IMed.Api.Domain.Catalogs;

namespace IMed.Api.Application.Abstractions;

public interface ISpecialtyRepository
{
    /// <summary>
    /// Búsqueda typeahead. Matchea por <c>Code</c> (prefix) o por <c>DisplayEs</c> (case-insensitive substring).
    /// Filtra <c>Active=true</c>. Limita resultados a <paramref name="limit"/>.
    /// </summary>
    Task<IReadOnlyList<SpecialtyCode>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);

    Task<SpecialtyCode?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);
}
