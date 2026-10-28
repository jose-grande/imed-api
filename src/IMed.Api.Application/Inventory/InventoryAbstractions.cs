using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;

namespace IMed.Api.Application.Inventory;

/// <summary>
/// Una pata de un movimiento que el libro tiene que registrar.
/// </summary>
/// <param name="UnitCost">
/// Costo de una entrada (compra, sobrante, devolución). En una salida va null: sale al promedio
/// vigente (D13). En la anulación de una compra va el costo de compra, para deshacer el promedio.
/// </param>
/// <param name="ReversesReceipt">
/// La salida deshace una entrada de compra: el promedio se recalcula quitando lo que entró a su
/// costo, en vez de quedar igual como en una salida normal.
/// </param>
/// <param name="CostFromWarehouseId">
/// Entrada que llega al costo promedio de otra bodega en ese mismo instante: la pata de entrada de
/// un traslado. Se resuelve dentro de la misma operación, con las dos filas de costo ya bloqueadas.
/// </param>
public sealed record StockPosting(
    StockMovementType Type,
    Guid StockItemId,
    Guid StockLotId,
    Guid WarehouseId,
    int Direction,
    decimal Quantity,
    Guid? SourceLineId = null,
    decimal? UnitCost = null,
    bool ReversesReceipt = false,
    Guid? ReversalOfMovementId = null,
    string? Notes = null,
    Guid? CostFromWarehouseId = null);

/// <summary>
/// 🔴 El libro de movimientos (FSF §8). Único punto por el que cambia una existencia.
///
/// <para><b>Contrato:</b> se llama dentro de una transacción (<see cref="IInventoryUnitOfWork"/>).
/// Bloquea primero, en un orden fijo, las filas de costo de cada (artículo, bodega) involucrado
/// —así dos salidas de la última unidad se atienden de a una y no hay interbloqueos—, aplica cada
/// pata al saldo (una salida sin existencia lanza <see cref="InsufficientStockException"/> y no deja
/// nada a medias), recalcula el promedio en las entradas y agrega las filas del libro.</para>
/// </summary>
public interface IStockLedger
{
    Task<IReadOnlyList<StockMovement>> PostAsync(
        Guid organizationId,
        Guid groupId,
        string sourceKind,
        Guid sourceId,
        Guid? performedBy,
        IReadOnlyList<StockPosting> postings,
        CancellationToken ct = default);

    /// <summary>Costo promedio vigente de un artículo en una bodega (0 si nunca tuvo existencia).</summary>
    Task<decimal> GetAverageCostAsync(Guid warehouseId, Guid stockItemId, CancellationToken ct = default);
}

/// <summary>
/// Transacción del inventario. Todo acto que mueve el libro corre adentro: el documento, sus
/// movimientos y —en farmacia— el cargo, se guardan juntos o no se guarda nada.
///
/// <para>Si ya hay una transacción abierta (otro módulo llamó a este dentro de la suya, como la nota
/// de crédito al puerto de devolución), se une a ella y deja que la cierre quien la abrió.</para>
/// </summary>
public interface IInventoryUnitOfWork
{
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct = default);

    /// <summary>Guarda lo pendiente dentro de la transacción en curso.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);

    /// <summary>
    /// Suelta lo pendiente después de un guardado que falló: si no, el siguiente guardado del mismo
    /// request lo reintentaría.
    /// </summary>
    void DiscardChanges();

    /// <summary>
    /// ¿El error es la violación de un índice único? Lo usan los caminos idempotentes para
    /// distinguir "otro ya lo hizo" de un fallo real, sin que la aplicación conozca a Postgres.
    /// </summary>
    bool IsUniqueViolation(Exception ex);
}

/// <summary>Persistencia de los agregados de inventario y farmacia. Lecturas estrictas por organización.</summary>
public interface IInventoryRepository
{
    // ── Configuración ──
    Task<InventorySettings> GetSettingsAsync(Guid organizationId, CancellationToken ct = default);
    Task SaveSettingsAsync(InventorySettings settings, CancellationToken ct = default);

    // ── Artículos ──
    Task<StockItem?> GetItemAsync(Guid organizationId, Guid itemId, CancellationToken ct = default);
    Task<IReadOnlyList<StockItem>> GetItemsAsync(Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<bool> ItemCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task<IReadOnlyList<StockItem>> SearchItemsAsync(
        Guid organizationId, string? term, StockItemKind? kind, bool? overTheCounter, bool activeOnly, int limit,
        CancellationToken ct = default);
    Task<IReadOnlyList<StockItem>> FindMedicationsByAtcAsync(Guid organizationId, string atcCode, CancellationToken ct = default);
    Task<IReadOnlyList<StockItem>> ListSellableWithoutBillableAsync(Guid organizationId, CancellationToken ct = default);
    Task AddItemAsync(StockItem item, CancellationToken ct = default);
    Task UpdateItemAsync(StockItem item, CancellationToken ct = default);

    // ── Bodegas ──
    Task<Warehouse?> GetWarehouseAsync(Guid organizationId, Guid warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<Warehouse>> ListWarehousesAsync(Guid organizationId, Guid? locationId, bool activeOnly, CancellationToken ct = default);
    Task<bool> WarehouseCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default);
    Task AddWarehouseAsync(Warehouse warehouse, CancellationToken ct = default);

    /// <summary>Agrega la bodega general salvo que la sede ya tenga una. El índice único decide.</summary>
    Task<bool> TryAddGeneralWarehouseAsync(Warehouse warehouse, CancellationToken ct = default);
    Task UpdateWarehouseAsync(Warehouse warehouse, CancellationToken ct = default);

    /// <summary>Organizaciones que tienen sedes, para el backfill de bodegas generales.</summary>
    Task<IReadOnlyList<Guid>> ListOrganizationsWithLocationsAsync(CancellationToken ct = default);

    // ── Proveedores ──
    Task<Supplier?> GetSupplierAsync(Guid organizationId, Guid supplierId, CancellationToken ct = default);
    Task<IReadOnlyList<Supplier>> ListSuppliersAsync(Guid organizationId, string? term, bool activeOnly, CancellationToken ct = default);
    Task<bool> SupplierTaxIdExistsAsync(Guid organizationId, string taxId, Guid? exceptId, CancellationToken ct = default);
    Task AddSupplierAsync(Supplier supplier, CancellationToken ct = default);
    Task UpdateSupplierAsync(Supplier supplier, CancellationToken ct = default);

    Task<IReadOnlyList<InventoryUnit>> ListUnitsAsync(Guid organizationId, CancellationToken ct = default);
    Task<InventoryUnit?> GetUnitAsync(Guid organizationId, Guid unitId, CancellationToken ct = default);
    Task<InventoryUnit?> FindUnitByNameAsync(Guid organizationId, string name, CancellationToken ct = default);

    /// <summary>Agrega la unidad salvo que ya exista con ese nombre (índice único). Guarda.</summary>
    Task<bool> TryAddUnitAsync(InventoryUnit unit, CancellationToken ct = default);

    /// <summary>Unidades base distintas que ya usan los artículos, para que entren a la lista.</summary>
    Task<IReadOnlyList<string>> ListItemBaseUnitsAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Renombra la unidad base en todos los artículos que la usan. Devuelve cuántos.</summary>
    Task<int> RenameItemsBaseUnitAsync(Guid organizationId, string oldName, string newName, CancellationToken ct = default);

    // ── Lotes ──
    Task<StockLot?> GetLotAsync(Guid organizationId, Guid lotId, CancellationToken ct = default);
    Task<IReadOnlyList<StockLot>> GetLotsAsync(Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
    Task<StockLot?> FindLotAsync(Guid stockItemId, string lotCode, CancellationToken ct = default);

    /// <summary>El lote de un artículo con ese código, creándolo si no existe. Idempotente bajo concurrencia.</summary>
    Task<StockLot> GetOrCreateLotAsync(
        Guid organizationId, Guid stockItemId, string lotCode, DateOnly? expiryDate, CancellationToken ct = default);

    /// <summary>El lote técnico de un artículo sin lote, creándolo si no existe.</summary>
    Task<StockLot> GetOrCreateUntrackedLotAsync(Guid organizationId, Guid stockItemId, CancellationToken ct = default);

    Task UpdateLotAsync(StockLot lot, CancellationToken ct = default);

    /// <summary>Lotes con existencia de un artículo en una bodega, para la salida FEFO.</summary>
    Task<IReadOnlyList<LotAvailability>> GetAvailabilityAsync(
        Guid warehouseId, Guid stockItemId, CancellationToken ct = default);

    // ── Documentos ──
    Task AddReceiptAsync(GoodsReceipt receipt, CancellationToken ct = default);
    Task<GoodsReceipt?> GetReceiptAsync(Guid organizationId, Guid receiptId, CancellationToken ct = default);
    Task<bool> ReceiptExistsAsync(Guid organizationId, Guid supplierId, string supplierInvoiceNumber, CancellationToken ct = default);
    Task AddTransferAsync(StockTransfer transfer, CancellationToken ct = default);
    Task<StockTransfer?> GetTransferAsync(Guid organizationId, Guid transferId, CancellationToken ct = default);
    Task AddConsumptionAsync(InternalConsumption consumption, CancellationToken ct = default);
    Task<InternalConsumption?> GetConsumptionAsync(Guid organizationId, Guid consumptionId, CancellationToken ct = default);
    Task AddAdjustmentAsync(StockAdjustment adjustment, CancellationToken ct = default);
    Task<StockAdjustment?> GetAdjustmentAsync(Guid organizationId, Guid adjustmentId, CancellationToken ct = default);
    Task AddCountAsync(StockCount count, CancellationToken ct = default);
    Task<StockCount?> GetCountAsync(Guid organizationId, Guid countId, CancellationToken ct = default);

    /// <summary>Existencias por lote de una bodega, para abrir un conteo.</summary>
    Task<IReadOnlyList<(Guid StockItemId, Guid StockLotId, decimal Quantity)>> GetWarehouseBalancesAsync(
        Guid warehouseId, CancellationToken ct = default);

    /// <summary>Existencia de un lote en una bodega (0 si no hay fila).</summary>
    Task<decimal> GetLotBalanceAsync(Guid warehouseId, Guid stockLotId, CancellationToken ct = default);

    // ── Farmacia ──
    Task AddDispenseAsync(PharmacyDispense dispense, CancellationToken ct = default);
    Task<PharmacyDispense?> GetDispenseAsync(Guid organizationId, Guid dispenseId, CancellationToken ct = default);
    Task<PharmacyDispense?> FindDispenseByKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken ct = default);
    Task<PharmacyDispense?> FindDispenseByLineAsync(Guid organizationId, Guid lineId, CancellationToken ct = default);

    /// <summary>Lo ya despachado de cada receta: total entregado y si alguna entrega la dio por atendida.</summary>
    Task<IReadOnlyDictionary<string, (decimal Dispensed, bool Completed)>> GetPrescriptionFillAsync(
        Guid organizationId, IReadOnlyCollection<string> medicationRequestIds, CancellationToken ct = default);

    Task<IReadOnlyList<PharmacyDispense>> ListDispensesAsync(
        Guid organizationId, Guid? locationId, PharmacyDispenseKind? kind, DateTimeOffset fromInclusive, DateTimeOffset toExclusive,
        int limit, CancellationToken ct = default);

    Task<bool> ReturnRecordExistsAsync(Guid organizationId, Guid creditNoteId, Guid chargeId, CancellationToken ct = default);
    Task AddReturnRecordAsync(StockReturnRecord record, CancellationToken ct = default);
}
