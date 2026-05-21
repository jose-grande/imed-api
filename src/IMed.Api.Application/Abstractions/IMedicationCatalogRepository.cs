using IMed.Api.Domain.Catalogs;

namespace IMed.Api.Application.Abstractions;

public interface IMedicationCatalogRepository
{
    /// <summary>
    /// Búsqueda typeahead del slice local de medicamentos. Match por ATC code
    /// (prefix exact) o por <c>NameGeneric</c>/<c>NameCommercial</c> con
    /// ILIKE + unaccent (mismo patrón que <see cref="IAllergyCatalogRepository"/>).
    /// Filtra opcionalmente por <paramref name="category"/>.
    /// </summary>
    Task<IReadOnlyList<MedicationCatalogEntry>> SearchAsync(
        string query,
        int limit,
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<MedicationCatalogEntry?> GetByAtcCodeAsync(string atcCode, CancellationToken cancellationToken = default);

    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
