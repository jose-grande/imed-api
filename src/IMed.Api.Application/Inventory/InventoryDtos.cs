using IMed.Api.Domain.Inventory;

namespace IMed.Api.Application.Inventory;

// Contratos de lectura del inventario (plan de salida a producción, B3 y B7). Viajan tal cual al
// frontend: los enums como texto, las fechas de vencimiento como día de calendario.

public sealed record PresentationDto(Guid Id, string Name, decimal FactorToBase);

public sealed record StockItemDto(
    Guid Id,
    string Code,
    string Name,
    string? Barcode,
    string Kind,
    string BaseUnit,
    bool RequiresLot,
    bool Sellable,
    bool OverTheCounter,
    Guid? TaxCodeId,
    Guid? BillableItemId,
    decimal? MinimumStock,
    bool Active,
    string? AtcCode,
    string? ActiveIngredient,
    string? Concentration,
    string? DoseForm,
    string? Route,
    string? SanitaryRegistration,
    string? StorageConditions,
    int? OpenStabilityDays,
    bool? Sterile,
    bool? SingleUse,
    IReadOnlyList<PresentationDto> Presentations,
    // Precio de referencia del catálogo (D12): solo en el detalle, null en los listados.
    decimal? Price = null,
    bool? PricePending = null);

public static class StockItemMapper
{
    public static StockItemDto ToDto(StockItem i, decimal? price = null, bool? pricePending = null) => new(
        i.Id, i.Code, i.Name, i.Barcode, i.Kind.ToString(), i.BaseUnit, i.RequiresLot, i.Sellable, i.OverTheCounter,
        i.TaxCodeId, i.BillableItemId, i.MinimumStock, i.Active,
        i.Medication?.AtcCode, i.Medication?.ActiveIngredient, i.Medication?.Concentration, i.Medication?.DoseForm,
        i.Medication?.Route, i.Medication?.SanitaryRegistration,
        i.Reagent?.StorageConditions, i.Reagent?.OpenStabilityDays,
        i.Supply?.Sterile, i.Supply?.SingleUse,
        i.Presentations.Select(p => new PresentationDto(p.Id, p.Name, p.FactorToBase)).ToList(),
        price, pricePending);
}

public sealed record WarehouseDto(
    Guid Id, Guid LocationId, string? LocationName, string Code, string Name, string Kind, bool IsGeneral, bool Active);

public sealed record SupplierDto(
    Guid Id, string Name, string? TaxId, string? ContactName, string? Phone, string? Email, bool Active);

public sealed record InventorySettingsDto(decimal AdjustmentApprovalThreshold, int ExpiryAlertDays);

/// <summary>Existencia de un lote en una bodega, valorizada al promedio de la bodega.</summary>
public sealed record StockLineDto(
    Guid WarehouseId,
    string WarehouseName,
    Guid StockItemId,
    string ItemCode,
    string ItemName,
    string BaseUnit,
    Guid StockLotId,
    string LotCode,
    DateOnly? ExpiryDate,
    string LotStatus,
    decimal QuantityOnHand,
    decimal AverageCost,
    decimal Value);

/// <summary>Un renglón del kardex: el movimiento y cómo quedó el artículo en la bodega.</summary>
public sealed record KardexEntryDto(
    Guid MovementId,
    DateTimeOffset OccurredAt,
    string Type,
    int Direction,
    decimal Quantity,
    string LotCode,
    DateOnly? ExpiryDate,
    decimal UnitCost,
    decimal TotalCost,
    decimal QuantityAfter,
    decimal AverageCostAfter,
    string SourceKind,
    Guid SourceId,
    string? Notes);

public sealed record LotProposalDto(Guid StockLotId, string LotCode, DateOnly? ExpiryDate, decimal Available, decimal Quantity);

/// <summary>Propuesta FEFO para sacar una cantidad de un artículo de una bodega.</summary>
public sealed record FefoProposalDto(
    Guid StockItemId, Guid WarehouseId, decimal Requested, decimal Usable, bool Sufficient,
    IReadOnlyList<LotProposalDto> Lots);

public sealed record DocumentLineDto(
    Guid Id, Guid StockItemId, string ItemCode, string ItemName, string BaseUnit, Guid StockLotId, string LotCode,
    DateOnly? ExpiryDate, int Direction, decimal Quantity, decimal UnitCost);

public sealed record GoodsReceiptLineDto(
    Guid Id, int LineNumber, Guid StockItemId, string ItemCode, string ItemName, string BaseUnit, Guid StockLotId,
    string LotCode, DateOnly? ExpiryDate, string? PresentationName, decimal PresentationQuantity, decimal FactorToBase,
    decimal Quantity, decimal UnitCost, decimal LineTotal);

public sealed record GoodsReceiptDto(
    Guid Id, Guid WarehouseId, string WarehouseName, Guid SupplierId, string SupplierName,
    string SupplierInvoiceNumber, DateOnly SupplierInvoiceDate, DateTimeOffset ReceivedAt, string? Notes,
    decimal Total, bool Voided, string? VoidReason, IReadOnlyList<GoodsReceiptLineDto> Lines);

public sealed record TransferDto(
    Guid Id, Guid FromWarehouseId, string FromWarehouseName, Guid ToWarehouseId, string ToWarehouseName,
    DateTimeOffset TransferredAt, string? Notes, IReadOnlyList<DocumentLineDto> Lines);

public sealed record ConsumptionDto(
    Guid Id, Guid WarehouseId, string WarehouseName, string Area, string? Notes, DateTimeOffset ConsumedAt,
    decimal Total, IReadOnlyList<DocumentLineDto> Lines);

public sealed record AdjustmentDto(
    Guid Id, Guid WarehouseId, string WarehouseName, string Reason, string? Notes, string Status, decimal Value,
    DateTimeOffset RequestedAt, Guid? RequestedBy, DateTimeOffset? DecidedAt, Guid? DecidedBy,
    string? RejectionReason, Guid? StockCountId, IReadOnlyList<DocumentLineDto> Lines);

public sealed record CountLineDto(
    Guid Id, Guid StockItemId, string ItemCode, string ItemName, string BaseUnit, Guid StockLotId, string LotCode,
    DateOnly? ExpiryDate, decimal ExpectedQuantity, decimal? CountedQuantity);

public sealed record CountDto(
    Guid Id, Guid WarehouseId, string WarehouseName, string Status, string? Notes, DateTimeOffset StartedAt,
    DateTimeOffset? ClosedAt, Guid? AdjustmentId, IReadOnlyList<CountLineDto> Lines);

/// <summary>Resumen de un documento para los listados.</summary>
public sealed record StockDocumentSummaryDto(
    Guid Id, string DocumentKind, DateTimeOffset Date, string WarehouseName, string? Counterpart,
    string? Reference, string Status, decimal Value, int LineCount);

public sealed record ExpiringLotDto(
    Guid WarehouseId, string WarehouseName, Guid StockItemId, string ItemCode, string ItemName, string BaseUnit,
    Guid StockLotId, string LotCode, DateOnly ExpiryDate, int DaysToExpiry, decimal QuantityOnHand, decimal Value);

public sealed record BelowMinimumDto(
    Guid StockItemId, string ItemCode, string ItemName, string BaseUnit, decimal Minimum, decimal OnHand);

/// <summary>Alertas de la sede (P9): vencimientos y existencias bajo el mínimo. Sin sugerencia de compra.</summary>
public sealed record InventoryAlertsDto(
    int ExpiryAlertDays,
    IReadOnlyList<ExpiringLotDto> Expired,
    IReadOnlyList<ExpiringLotDto> Expiring,
    IReadOnlyList<BelowMinimumDto> BelowMinimum,
    int PendingAdjustments);

public sealed record ValuationRowDto(
    Guid WarehouseId, string WarehouseName, Guid StockItemId, string ItemCode, string ItemName, string Kind,
    string BaseUnit, decimal QuantityOnHand, decimal AverageCost, decimal Value);

public sealed record ValuationWarehouseDto(Guid WarehouseId, string WarehouseName, string? LocationName, decimal Value, int ItemCount);

public sealed record ValuationReportDto(
    DateTimeOffset GeneratedAt, decimal Total, IReadOnlyList<ValuationWarehouseDto> Warehouses, IReadOnlyList<ValuationRowDto> Rows);

public sealed record MovementReportRowDto(
    Guid MovementId, DateTimeOffset OccurredAt, string WarehouseName, string ItemCode, string ItemName,
    string BaseUnit, string LotCode, string Type, int Direction, decimal Quantity, decimal UnitCost, decimal TotalCost,
    string SourceKind, Guid SourceId);

public sealed record MovementReportDto(
    DateOnly From, DateOnly To, decimal TotalIn, decimal TotalOut, IReadOnlyList<MovementReportRowDto> Rows);

public sealed record ConsumptionReportRowDto(
    string Area, string WarehouseName, Guid StockItemId, string ItemCode, string ItemName, string BaseUnit,
    decimal Quantity, decimal Cost);

public sealed record ConsumptionReportDto(
    DateOnly From, DateOnly To, decimal TotalCost, IReadOnlyList<ConsumptionReportRowDto> Rows);

/// <summary>Lecturas de pantallas y reportes, proyectadas en la base (B3, B7).</summary>
public interface IInventoryQueries
{
    Task<IReadOnlyList<StockLineDto>> GetStockAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, Guid? stockItemId, string? term, bool includeEmpty,
        CancellationToken ct = default);

    Task<IReadOnlyList<KardexEntryDto>> GetKardexAsync(
        Guid organizationId, Guid warehouseId, Guid stockItemId, DateTimeOffset? fromInclusive, DateTimeOffset? toExclusive,
        CancellationToken ct = default);

    Task<IReadOnlyList<StockDocumentSummaryDto>> ListDocumentsAsync(
        Guid organizationId, string documentKind, Guid? locationId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExpiringLotDto>> GetExpiringLotsAsync(
        Guid organizationId, Guid? locationId, DateOnly today, DateOnly until, CancellationToken ct = default);

    Task<IReadOnlyList<BelowMinimumDto>> GetBelowMinimumAsync(
        Guid organizationId, Guid locationId, CancellationToken ct = default);

    Task<int> CountPendingAdjustmentsAsync(Guid organizationId, Guid? locationId, CancellationToken ct = default);

    Task<IReadOnlyList<ValuationRowDto>> GetValuationAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, CancellationToken ct = default);

    Task<IReadOnlyList<MovementReportRowDto>> GetMovementsAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, Guid? stockItemId, StockMovementType? type,
        DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int limit, CancellationToken ct = default);

    Task<IReadOnlyList<ConsumptionReportRowDto>> GetConsumptionAsync(
        Guid organizationId, Guid? locationId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken ct = default);

    /// <summary>Datos de cabecera para pintar documentos: nombres de artículos, lotes y bodegas.</summary>
    Task<DocumentLookup> GetLookupAsync(
        Guid organizationId, IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> lotIds,
        IReadOnlyCollection<Guid> warehouseIds, CancellationToken ct = default);
}

public sealed record DocumentLookup(
    IReadOnlyDictionary<Guid, (string Code, string Name, string BaseUnit)> Items,
    IReadOnlyDictionary<Guid, (string LotCode, DateOnly? Expiry)> Lots,
    IReadOnlyDictionary<Guid, string> Warehouses)
{
    public DocumentLineDto Line(Guid id, Guid itemId, Guid lotId, int direction, decimal quantity, decimal unitCost)
    {
        var item = Items.TryGetValue(itemId, out var i) ? i : (Code: "?", Name: "Artículo desconocido", BaseUnit: "");
        var lot = Lots.TryGetValue(lotId, out var l) ? l : (LotCode: "?", Expiry: (DateOnly?)null);
        return new DocumentLineDto(id, itemId, item.Code, item.Name, item.BaseUnit, lotId, lot.LotCode, lot.Expiry,
            direction, quantity, unitCost);
    }

    public string Warehouse(Guid id) => Warehouses.GetValueOrDefault(id, "Bodega desconocida");
}
