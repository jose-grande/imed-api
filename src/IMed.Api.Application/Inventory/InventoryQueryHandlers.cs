using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Inventory;

namespace IMed.Api.Application.Inventory;

/// <summary>
/// Lecturas de las pantallas de inventario (B3): existencias, kardex, propuesta FEFO, documentos y
/// alertas (P9). Todo por sede: lo que se muestra es la sede activa salvo que se pida una bodega.
/// </summary>
public sealed class InventoryQueryHandlers(
    IInventoryRepository repo,
    IInventoryQueries queries,
    WarehouseResolver warehouses,
    WarehouseProvisioner provisioner,
    ResolveActiveLocationHandler activeLocation,
    IClinicClock clock)
{
    public async Task<IReadOnlyList<StockLineDto>> StockAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, Guid? stockItemId, string? term, bool allLocations,
        bool includeEmpty, CancellationToken ct = default)
    {
        await provisioner.EnsureGeneralWarehousesAsync(organizationId, ct);
        Guid? sede = null;
        if (warehouseId is { } w)
        {
            await warehouses.ResolveAsync(organizationId, userId, w, false, ct);
        }
        else if (!allLocations)
        {
            sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        }
        return await queries.GetStockAsync(organizationId, sede, warehouseId, stockItemId, term, includeEmpty, ct);
    }

    public async Task<IReadOnlyList<KardexEntryDto>> KardexAsync(
        Guid organizationId, Guid? userId, Guid warehouseId, Guid stockItemId, DateOnly? from, DateOnly? to,
        CancellationToken ct = default)
    {
        await warehouses.ResolveAsync(organizationId, userId, warehouseId, false, ct);
        return await queries.GetKardexAsync(
            organizationId, warehouseId, stockItemId,
            from is { } f ? clock.StartOf(f) : null,
            to is { } t ? clock.StartOf(t.AddDays(1)) : null, ct);
    }

    /// <summary>Qué lotes saldrían para despachar <paramref name="quantity"/> (FEFO), sin mover nada.</summary>
    public async Task<FefoProposalDto> FefoAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, Guid stockItemId, decimal quantity, bool preferPharmacy,
        CancellationToken ct = default)
    {
        var bodega = await warehouses.ResolveAsync(organizationId, userId, warehouseId, preferPharmacy, ct);
        var item = await repo.GetItemAsync(organizationId, stockItemId, ct)
            ?? throw new ArgumentException("El artículo no existe.");
        var hoy = clock.Today();
        var lotes = await repo.GetAvailabilityAsync(bodega.Id, item.Id, ct);
        var usable = FefoAllocator.Usable(lotes, hoy);
        var reparto = quantity > 0 ? FefoAllocator.Allocate(lotes, quantity, hoy) : null;
        var propuesta = FefoAllocator.Order(lotes, hoy)
            .Select(l => new LotProposalDto(
                l.StockLotId, l.LotCode, l.ExpiryDate, l.Available,
                reparto?.FirstOrDefault(a => a.StockLotId == l.StockLotId)?.Quantity ?? 0m))
            .ToList();
        return new FefoProposalDto(item.Id, bodega.Id, quantity, usable, quantity > 0 && reparto is not null, propuesta);
    }

    public async Task<InventoryAlertsDto> AlertsAsync(Guid organizationId, Guid? userId, CancellationToken ct = default)
    {
        await provisioner.EnsureGeneralWarehousesAsync(organizationId, ct);
        var sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        var ajustes = await repo.GetSettingsAsync(organizationId, ct);
        var hoy = clock.Today();
        var lotes = await queries.GetExpiringLotsAsync(organizationId, sede, hoy, hoy.AddDays(ajustes.ExpiryAlertDays), ct);
        return new InventoryAlertsDto(
            ajustes.ExpiryAlertDays,
            lotes.Where(l => l.DaysToExpiry < 0).ToList(),
            lotes.Where(l => l.DaysToExpiry >= 0).ToList(),
            await queries.GetBelowMinimumAsync(organizationId, sede, ct),
            await queries.CountPendingAdjustmentsAsync(organizationId, sede, ct));
    }

    public async Task<IReadOnlyList<StockDocumentSummaryDto>> DocumentsAsync(
        Guid organizationId, Guid? userId, string kind, DateOnly? from, DateOnly? to, bool allLocations,
        CancellationToken ct = default)
    {
        var hoy = clock.Today();
        var desde = from ?? hoy.AddDays(-30);
        var hasta = to ?? hoy;
        Guid? sede = allLocations ? null : await activeLocation.HandleAsync(organizationId, userId, null, ct);
        return await queries.ListDocumentsAsync(
            organizationId, kind, sede, clock.StartOf(desde), clock.StartOf(hasta.AddDays(1)), 500, ct);
    }

    public async Task<GoodsReceiptDto?> ReceiptAsync(Guid organizationId, Guid id, CancellationToken ct = default)
    {
        var e = await repo.GetReceiptAsync(organizationId, id, ct);
        if (e is null)
        {
            return null;
        }
        var proveedor = await repo.GetSupplierAsync(organizationId, e.SupplierId, ct);
        var lk = await queries.GetLookupAsync(
            organizationId, e.Lines.Select(l => l.StockItemId).ToList(), e.Lines.Select(l => l.StockLotId).ToList(),
            [e.WarehouseId], ct);
        return new GoodsReceiptDto(
            e.Id, e.WarehouseId, lk.Warehouse(e.WarehouseId), e.SupplierId, proveedor?.Name ?? "?", e.SupplierInvoiceNumber,
            e.SupplierInvoiceDate, e.ReceivedAt, e.Notes, e.Total, e.Voided, e.VoidReason,
            e.Lines.OrderBy(l => l.LineNumber).Select(l =>
            {
                var d = lk.Line(l.Id, l.StockItemId, l.StockLotId, 1, l.Quantity, l.UnitCost);
                return new GoodsReceiptLineDto(
                    l.Id, l.LineNumber, l.StockItemId, d.ItemCode, d.ItemName, d.BaseUnit, l.StockLotId, d.LotCode,
                    d.ExpiryDate, l.PresentationName, l.PresentationQuantity, l.FactorToBase, l.Quantity, l.UnitCost,
                    l.LineTotal);
            }).ToList());
    }

    public async Task<TransferDto?> TransferAsync(Guid organizationId, Guid id, CancellationToken ct = default)
    {
        var t = await repo.GetTransferAsync(organizationId, id, ct);
        if (t is null)
        {
            return null;
        }
        var lk = await LookupAsync(organizationId, t.Lines, [t.FromWarehouseId, t.ToWarehouseId], ct);
        return new TransferDto(
            t.Id, t.FromWarehouseId, lk.Warehouse(t.FromWarehouseId), t.ToWarehouseId, lk.Warehouse(t.ToWarehouseId),
            t.TransferredAt, t.Notes, Lines(lk, t.Lines));
    }

    public async Task<ConsumptionDto?> ConsumptionAsync(Guid organizationId, Guid id, CancellationToken ct = default)
    {
        var c = await repo.GetConsumptionAsync(organizationId, id, ct);
        if (c is null)
        {
            return null;
        }
        var lk = await LookupAsync(organizationId, c.Lines, [c.WarehouseId], ct);
        return new ConsumptionDto(
            c.Id, c.WarehouseId, lk.Warehouse(c.WarehouseId), c.Area, c.Notes, c.ConsumedAt,
            Math.Round(c.Lines.Sum(l => l.Quantity * l.UnitCost), 2, MidpointRounding.AwayFromZero), Lines(lk, c.Lines));
    }

    public async Task<AdjustmentDto?> AdjustmentAsync(Guid organizationId, Guid id, CancellationToken ct = default)
    {
        var a = await repo.GetAdjustmentAsync(organizationId, id, ct);
        if (a is null)
        {
            return null;
        }
        var lk = await LookupAsync(organizationId, a.Lines, [a.WarehouseId], ct);
        return new AdjustmentDto(
            a.Id, a.WarehouseId, lk.Warehouse(a.WarehouseId), a.Reason.ToString(), a.Notes, a.Status.ToString(), a.Value,
            a.RequestedAt, a.RequestedBy, a.DecidedAt, a.DecidedBy, a.RejectionReason, a.StockCountId, Lines(lk, a.Lines));
    }

    public async Task<CountDto?> CountAsync(Guid organizationId, Guid id, CancellationToken ct = default)
    {
        var c = await repo.GetCountAsync(organizationId, id, ct);
        if (c is null)
        {
            return null;
        }
        var lk = await queries.GetLookupAsync(
            organizationId, c.Lines.Select(l => l.StockItemId).ToList(), c.Lines.Select(l => l.StockLotId).ToList(),
            [c.WarehouseId], ct);
        return new CountDto(
            c.Id, c.WarehouseId, lk.Warehouse(c.WarehouseId), c.Status.ToString(), c.Notes, c.StartedAt, c.ClosedAt,
            c.AdjustmentId,
            c.Lines.Select(l =>
            {
                var d = lk.Line(l.Id, l.StockItemId, l.StockLotId, 1, l.ExpectedQuantity, 0);
                return new CountLineDto(l.Id, l.StockItemId, d.ItemCode, d.ItemName, d.BaseUnit, l.StockLotId, d.LotCode,
                    d.ExpiryDate, l.ExpectedQuantity, l.CountedQuantity);
            }).OrderBy(l => l.ItemName).ThenBy(l => l.ExpiryDate).ToList());
    }

    private Task<DocumentLookup> LookupAsync(
        Guid organizationId, IReadOnlyList<StockDocumentLine> lines, Guid[] warehouseIds, CancellationToken ct) =>
        queries.GetLookupAsync(
            organizationId, lines.Select(l => l.StockItemId).ToList(), lines.Select(l => l.StockLotId).ToList(),
            warehouseIds, ct);

    private static List<DocumentLineDto> Lines(DocumentLookup lk, IReadOnlyList<StockDocumentLine> lines) =>
        lines.OrderBy(l => l.LineNumber)
            .Select(l => lk.Line(l.Id, l.StockItemId, l.StockLotId, l.Direction, l.Quantity, l.UnitCost))
            .ToList();
}

/// <summary>
/// Reportes de inventario (B7). Los cortes de día son de la clínica (<see cref="IClinicClock.StartOf"/>):
/// cortar en la medianoche UTC deja las últimas seis horas de la tarde hondureña en el día siguiente.
/// </summary>
public sealed class InventoryReportHandlers(
    IInventoryQueries queries,
    ResolveActiveLocationHandler activeLocation,
    WarehouseResolver warehouses,
    IClinicClock clock,
    TimeProvider time)
{
    public async Task<ValuationReportDto> ValuationAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, bool allLocations, CancellationToken ct = default)
    {
        var sede = await SedeAsync(organizationId, userId, warehouseId, allLocations, ct);
        var filas = await queries.GetValuationAsync(organizationId, sede, warehouseId, ct);
        var bodegas = filas.GroupBy(f => (f.WarehouseId, f.WarehouseName))
            .Select(g => new ValuationWarehouseDto(g.Key.WarehouseId, g.Key.WarehouseName, null,
                Math.Round(g.Sum(x => x.Value), 2, MidpointRounding.AwayFromZero), g.Count()))
            .OrderBy(b => b.WarehouseName)
            .ToList();
        return new ValuationReportDto(
            time.GetUtcNow(), Math.Round(filas.Sum(f => f.Value), 2, MidpointRounding.AwayFromZero), bodegas, filas);
    }

    public async Task<IReadOnlyList<ExpiringLotDto>> ExpiringAsync(
        Guid organizationId, Guid? userId, int days, bool allLocations, CancellationToken ct = default)
    {
        var sede = await SedeAsync(organizationId, userId, null, allLocations, ct);
        var hoy = clock.Today();
        return await queries.GetExpiringLotsAsync(organizationId, sede, hoy, hoy.AddDays(Math.Clamp(days, 0, 730)), ct);
    }

    public async Task<MovementReportDto> MovementsAsync(
        Guid organizationId, Guid? userId, DateOnly from, DateOnly to, Guid? warehouseId, Guid? stockItemId, string? type,
        bool allLocations, CancellationToken ct = default)
    {
        ValidarRango(from, to);
        var sede = await SedeAsync(organizationId, userId, warehouseId, allLocations, ct);
        StockMovementType? tipo = Enum.TryParse<StockMovementType>(type, ignoreCase: true, out var t) ? t : null;
        var filas = await queries.GetMovementsAsync(
            organizationId, sede, warehouseId, stockItemId, tipo, clock.StartOf(from), clock.StartOf(to.AddDays(1)), 5000, ct);
        return new MovementReportDto(
            from, to,
            Math.Round(filas.Where(f => f.Direction > 0).Sum(f => f.TotalCost), 2, MidpointRounding.AwayFromZero),
            Math.Round(filas.Where(f => f.Direction < 0).Sum(f => f.TotalCost), 2, MidpointRounding.AwayFromZero),
            filas);
    }

    public async Task<ConsumptionReportDto> ConsumptionAsync(
        Guid organizationId, Guid? userId, DateOnly from, DateOnly to, bool allLocations, CancellationToken ct = default)
    {
        ValidarRango(from, to);
        var sede = await SedeAsync(organizationId, userId, null, allLocations, ct);
        var filas = await queries.GetConsumptionAsync(organizationId, sede, clock.StartOf(from), clock.StartOf(to.AddDays(1)), ct);
        return new ConsumptionReportDto(from, to, Math.Round(filas.Sum(f => f.Cost), 2, MidpointRounding.AwayFromZero), filas);
    }

    private async Task<Guid?> SedeAsync(
        Guid organizationId, Guid? userId, Guid? warehouseId, bool allLocations, CancellationToken ct)
    {
        if (warehouseId is { } w)
        {
            await warehouses.ResolveAsync(organizationId, userId, w, false, ct);
            return null;
        }
        return allLocations ? null : await activeLocation.HandleAsync(organizationId, userId, null, ct);
    }

    private static void ValidarRango(DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            throw new ArgumentException("La fecha final es anterior a la inicial.");
        }
        if (to.DayNumber - from.DayNumber > 366)
        {
            throw new ArgumentException("El rango máximo es de un año.");
        }
    }
}
