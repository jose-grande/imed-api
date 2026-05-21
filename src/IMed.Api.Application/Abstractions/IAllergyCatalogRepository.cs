using IMed.Api.Domain.Catalogs;

namespace IMed.Api.Application.Abstractions;

public interface IAllergyCatalogRepository
{
    /// <summary>
    /// Búsqueda typeahead del slice local SNOMED de alergenos comunes.
    /// Filtra por activo + opcionalmente por <paramref name="category"/>
    /// (food/medication/environment/biologic/other). Match por display
    /// case-insensitive con ILIKE; el code es SNOMED numérico y matchea por
    /// prefix.
    /// </summary>
    Task<IReadOnlyList<AllergyCatalogEntry>> SearchAsync(
        string query,
        int limit,
        string? category = null,
        CancellationToken cancellationToken = default);

    Task<AllergyCatalogEntry?> GetByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Cantidad de entries en el catálogo. Lo usa el seeder para decidir si insertar.</summary>
    Task<int> CountAsync(CancellationToken cancellationToken = default);
}
