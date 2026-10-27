using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Application.Abstractions;

public interface IBillingCatalogRepository
{
    // ---- Cobrables ----
    Task<IReadOnlyList<BillableItem>> SearchItemsAsync(
        Guid organizationId, string? term, bool activeOnly, int limit, CancellationToken ct = default);
    Task<BillableItem?> GetItemAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task<bool> ItemCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task AddItemAsync(BillableItem item, CancellationToken ct = default);
    Task UpdateItemAsync(BillableItem item, CancellationToken ct = default);

    /// <summary>Cobrables activos que todavía no tienen precio (D1), para completarlos.</summary>
    Task<IReadOnlyList<BillableItem>> ListPricePendingItemsAsync(
        Guid organizationId, CancellationToken ct = default);

    /// <summary>El cobrable de la consulta de una especialidad y un tipo, activo o no.</summary>
    Task<BillableItem?> FindBySpecialtyAsync(
        Guid organizationId, string specialtyCode, ConsultationType type, CancellationToken ct = default);

    /// <summary>El cobrable enlazado a una prueba o a un panel, activo o no.</summary>
    Task<BillableItem?> FindAnyByLabAsync(
        Guid organizationId, Guid? labTestId, Guid? labPanelId, CancellationToken ct = default);

    /// <summary>El cobrable enlazado a un artículo de inventario, activo o no.</summary>
    Task<BillableItem?> FindAnyByStockItemAsync(
        Guid organizationId, Guid stockItemId, CancellationToken ct = default);

    /// <summary>
    /// Agrega un cobrable si su enlace (especialidad+tipo, prueba o panel) no tiene ya uno.
    /// Devuelve <c>false</c> si otro camino lo creó antes: el índice único decide.
    /// </summary>
    Task<bool> TryAddLinkedItemAsync(BillableItem item, CancellationToken ct = default);

    /// <summary>
    /// Cobrable enlazado a una prueba o panel del LIS. Es lo que consulta la captura automática de
    /// cargos (FAC-4) cuando el laboratorio admite una orden.
    /// </summary>
    Task<BillableItem?> FindByLabAsync(
        Guid organizationId, Guid? labTestId, Guid? labPanelId, CancellationToken ct = default);

    // ---- Tarifarios ----
    Task<IReadOnlyList<PriceList>> ListPriceListsAsync(Guid organizationId, CancellationToken ct = default);
    Task<PriceList?> GetPriceListAsync(Guid organizationId, Guid id, CancellationToken ct = default);
    Task<bool> PriceListCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task AddPriceListAsync(PriceList list, CancellationToken ct = default);
    Task UpdatePriceListAsync(PriceList list, CancellationToken ct = default);

    Task<IReadOnlyList<PriceListItem>> ListPriceListItemsAsync(
        Guid organizationId, Guid priceListId, CancellationToken ct = default);
    Task AddPriceListItemAsync(PriceListItem item, CancellationToken ct = default);

    /// <summary>
    /// Todos los precios de un cobrable, con su lista. Es lo que consume
    /// <see cref="Domain.Billing.Catalog.PriceResolver"/>: se traen juntos y se decide en memoria
    /// para que la regla de precedencia viva en un solo lugar y no repartida en un <c>ORDER BY</c>.
    /// </summary>
    Task<IReadOnlyList<PriceCandidate>> GetPriceCandidatesAsync(
        Guid organizationId, Guid billableItemId, CancellationToken ct = default);
}
