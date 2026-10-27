using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Inventory;

/// <summary>Lecturas de pantallas y reportes de inventario, proyectadas en la base.</summary>
public sealed class InventoryQueries(AppDbContext db) : IInventoryQueries
{
    private IQueryable<Warehouse> Bodegas(Guid organizationId, Guid? locationId, Guid? warehouseId)
    {
        var q = db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == organizationId);
        if (locationId is { } l)
        {
            q = q.Where(w => w.LocationId == l);
        }
        if (warehouseId is { } w2)
        {
            q = q.Where(w => w.Id == w2);
        }
        return q;
    }

    public async Task<IReadOnlyList<StockLineDto>> GetStockAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, Guid? stockItemId, string? term, bool includeEmpty,
        CancellationToken ct = default)
    {
        var q = from b in db.StockBalances.AsNoTracking()
                join w in Bodegas(organizationId, locationId, warehouseId) on b.WarehouseId equals w.Id
                join i in db.StockItems.AsNoTracking() on b.StockItemId equals i.Id
                join l in db.StockLots.AsNoTracking() on b.StockLotId equals l.Id
                join c in db.StockItemCosts.AsNoTracking()
                    on new { b.WarehouseId, b.StockItemId } equals new { c.WarehouseId, c.StockItemId } into costos
                from c in costos.DefaultIfEmpty()
                where b.OrganizationId == organizationId
                select new { b, w, i, l, Avg = c == null ? 0m : c.AverageCost };
        if (!includeEmpty)
        {
            q = q.Where(x => x.b.QuantityOnHand > 0);
        }
        if (stockItemId is { } item)
        {
            q = q.Where(x => x.i.Id == item);
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var patron = $"%{term.Trim()}%";
            q = q.Where(x => EF.Functions.ILike(x.i.Name, patron) || EF.Functions.ILike(x.i.Code, patron)
                             || EF.Functions.ILike(x.l.LotCode, patron));
        }
        var filas = await q.OrderBy(x => x.i.Name).ThenBy(x => x.w.Name).ThenBy(x => x.l.ExpiryDate)
            .Take(5000).ToListAsync(ct);
        return filas.Select(x => new StockLineDto(
            x.w.Id, x.w.Name, x.i.Id, x.i.Code, x.i.Name, x.i.BaseUnit, x.l.Id, x.l.LotCode, x.l.ExpiryDate,
            x.l.Status.ToString(), x.b.QuantityOnHand, x.Avg,
            Math.Round(x.b.QuantityOnHand * x.Avg, 2, MidpointRounding.AwayFromZero))).ToList();
    }

    public async Task<IReadOnlyList<KardexEntryDto>> GetKardexAsync(
        Guid organizationId, Guid warehouseId, Guid stockItemId, DateTimeOffset? fromInclusive, DateTimeOffset? toExclusive,
        CancellationToken ct = default)
    {
        var q = from m in db.StockMovements.AsNoTracking()
                join l in db.StockLots.AsNoTracking() on m.StockLotId equals l.Id
                where m.OrganizationId == organizationId && m.WarehouseId == warehouseId && m.StockItemId == stockItemId
                select new { m, l };
        if (fromInclusive is { } f)
        {
            q = q.Where(x => x.m.OccurredAt >= f);
        }
        if (toExclusive is { } t)
        {
            q = q.Where(x => x.m.OccurredAt < t);
        }
        var filas = await q.OrderBy(x => x.m.OccurredAt).ThenBy(x => x.m.Id).Take(5000).ToListAsync(ct);
        return filas.Select(x => new KardexEntryDto(
            x.m.Id, x.m.OccurredAt, x.m.Type.ToString(), x.m.Direction, x.m.Quantity, x.l.LotCode, x.l.ExpiryDate,
            x.m.UnitCost, x.m.TotalCost, x.m.QuantityAfter, x.m.AverageCostAfter, x.m.SourceKind, x.m.SourceId,
            x.m.Notes)).ToList();
    }

    public async Task<IReadOnlyList<StockDocumentSummaryDto>> ListDocumentsAsync(
        Guid organizationId, string documentKind, Guid? locationId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive,
        int limit, CancellationToken ct = default)
    {
        var bodegas = await Bodegas(organizationId, locationId, null).ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var ids = bodegas.Keys.ToList();
        string Bodega(Guid id) => bodegas.GetValueOrDefault(id, "?");

        switch (documentKind.Trim().ToLowerInvariant())
        {
            case "receipts":
            {
                var filas = await (from r in db.GoodsReceipts.AsNoTracking()
                                   join s in db.Suppliers.AsNoTracking() on r.SupplierId equals s.Id
                                   where r.OrganizationId == organizationId && ids.Contains(r.WarehouseId)
                                         && r.ReceivedAt >= fromInclusive && r.ReceivedAt < toExclusive
                                   orderby r.ReceivedAt descending
                                   select new { r.Id, r.ReceivedAt, r.WarehouseId, Proveedor = s.Name, r.SupplierInvoiceNumber, r.Voided, r.Total, Lineas = r.Lines.Count })
                    .Take(limit).ToListAsync(ct);
                return filas.Select(x => new StockDocumentSummaryDto(
                    x.Id, "receipt", x.ReceivedAt, Bodega(x.WarehouseId), x.Proveedor, x.SupplierInvoiceNumber,
                    x.Voided ? "Voided" : "Posted", x.Total, x.Lineas)).ToList();
            }
            case "transfers":
            {
                var filas = await db.StockTransfers.AsNoTracking()
                    .Where(t => t.OrganizationId == organizationId
                                && (ids.Contains(t.FromWarehouseId) || ids.Contains(t.ToWarehouseId))
                                && t.TransferredAt >= fromInclusive && t.TransferredAt < toExclusive)
                    .OrderByDescending(t => t.TransferredAt)
                    .Select(t => new { t.Id, t.TransferredAt, t.FromWarehouseId, t.ToWarehouseId, t.Notes,
                        Valor = t.Lines.Sum(l => l.Quantity * l.UnitCost), Lineas = t.Lines.Count })
                    .Take(limit).ToListAsync(ct);
                var nombres = await db.Warehouses.AsNoTracking().Where(w => w.OrganizationId == organizationId)
                    .ToDictionaryAsync(w => w.Id, w => w.Name, ct);
                return filas.Select(x => new StockDocumentSummaryDto(
                    x.Id, "transfer", x.TransferredAt, nombres.GetValueOrDefault(x.FromWarehouseId, "?"),
                    nombres.GetValueOrDefault(x.ToWarehouseId, "?"), x.Notes, "Posted",
                    Math.Round(x.Valor, 2, MidpointRounding.AwayFromZero), x.Lineas)).ToList();
            }
            case "consumptions":
            {
                var filas = await db.InternalConsumptions.AsNoTracking()
                    .Where(c => c.OrganizationId == organizationId && ids.Contains(c.WarehouseId)
                                && c.ConsumedAt >= fromInclusive && c.ConsumedAt < toExclusive)
                    .OrderByDescending(c => c.ConsumedAt)
                    .Select(c => new { c.Id, c.ConsumedAt, c.WarehouseId, c.Area, c.Notes,
                        Valor = c.Lines.Sum(l => l.Quantity * l.UnitCost), Lineas = c.Lines.Count })
                    .Take(limit).ToListAsync(ct);
                return filas.Select(x => new StockDocumentSummaryDto(
                    x.Id, "consumption", x.ConsumedAt, Bodega(x.WarehouseId), x.Area, x.Notes, "Posted",
                    Math.Round(x.Valor, 2, MidpointRounding.AwayFromZero), x.Lineas)).ToList();
            }
            case "adjustments":
            {
                // Los pendientes de aprobación se ven siempre, sean de la fecha que sean.
                var filas = await db.StockAdjustments.AsNoTracking()
                    .Where(a => a.OrganizationId == organizationId && ids.Contains(a.WarehouseId)
                                && ((a.RequestedAt >= fromInclusive && a.RequestedAt < toExclusive)
                                    || a.Status == StockDocumentStatus.PendingApproval))
                    .OrderByDescending(a => a.RequestedAt)
                    .Select(a => new { a.Id, a.RequestedAt, a.WarehouseId, a.Reason, a.Notes, a.Status, a.Value, Lineas = a.Lines.Count })
                    .Take(limit).ToListAsync(ct);
                return filas.Select(x => new StockDocumentSummaryDto(
                    x.Id, "adjustment", x.RequestedAt, Bodega(x.WarehouseId), x.Reason.ToString(), x.Notes,
                    x.Status.ToString(), x.Value, x.Lineas)).ToList();
            }
            case "counts":
            {
                var filas = await db.StockCounts.AsNoTracking()
                    .Where(c => c.OrganizationId == organizationId && ids.Contains(c.WarehouseId)
                                && ((c.StartedAt >= fromInclusive && c.StartedAt < toExclusive) || c.Status == StockDocumentStatus.Draft))
                    .OrderByDescending(c => c.StartedAt)
                    .Select(c => new { c.Id, c.StartedAt, c.WarehouseId, c.Notes, c.Status, Lineas = c.Lines.Count })
                    .Take(limit).ToListAsync(ct);
                return filas.Select(x => new StockDocumentSummaryDto(
                    x.Id, "count", x.StartedAt, Bodega(x.WarehouseId), null, x.Notes, x.Status.ToString(), 0m, x.Lineas)).ToList();
            }
            default:
                throw new ArgumentException("Tipo de documento desconocido: receipts, transfers, consumptions, adjustments o counts.");
        }
    }

    public async Task<IReadOnlyList<ExpiringLotDto>> GetExpiringLotsAsync(
        Guid organizationId, Guid? locationId, DateOnly today, DateOnly until, CancellationToken ct = default)
    {
        var filas = await (from b in db.StockBalances.AsNoTracking()
                           join w in Bodegas(organizationId, locationId, null) on b.WarehouseId equals w.Id
                           join i in db.StockItems.AsNoTracking() on b.StockItemId equals i.Id
                           join l in db.StockLots.AsNoTracking() on b.StockLotId equals l.Id
                           join c in db.StockItemCosts.AsNoTracking()
                               on new { b.WarehouseId, b.StockItemId } equals new { c.WarehouseId, c.StockItemId } into costos
                           from c in costos.DefaultIfEmpty()
                           where b.QuantityOnHand > 0 && l.ExpiryDate != null && l.ExpiryDate <= until
                           orderby l.ExpiryDate, i.Name
                           select new { w, i, l, b.QuantityOnHand, Avg = c == null ? 0m : c.AverageCost })
            .Take(2000).ToListAsync(ct);
        return filas.Select(x => new ExpiringLotDto(
            x.w.Id, x.w.Name, x.i.Id, x.i.Code, x.i.Name, x.i.BaseUnit, x.l.Id, x.l.LotCode, x.l.ExpiryDate!.Value,
            x.l.ExpiryDate.Value.DayNumber - today.DayNumber, x.QuantityOnHand,
            Math.Round(x.QuantityOnHand * x.Avg, 2, MidpointRounding.AwayFromZero))).ToList();
    }

    public async Task<IReadOnlyList<BelowMinimumDto>> GetBelowMinimumAsync(
        Guid organizationId, Guid locationId, CancellationToken ct = default)
    {
        var bodegas = Bodegas(organizationId, locationId, null).Select(w => w.Id);
        var filas = await db.StockItems.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.Active && i.MinimumStock != null)
            .Select(i => new
            {
                i.Id, i.Code, i.Name, i.BaseUnit, Minimo = i.MinimumStock!.Value,
                Hay = db.StockBalances.Where(b => b.StockItemId == i.Id && bodegas.Contains(b.WarehouseId))
                    .Sum(b => (decimal?)b.QuantityOnHand) ?? 0m,
            })
            .Where(x => x.Hay < x.Minimo)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return filas.Select(x => new BelowMinimumDto(x.Id, x.Code, x.Name, x.BaseUnit, x.Minimo, x.Hay)).ToList();
    }

    public Task<int> CountPendingAdjustmentsAsync(Guid organizationId, Guid? locationId, CancellationToken ct = default)
    {
        var bodegas = Bodegas(organizationId, locationId, null).Select(w => w.Id);
        return db.StockAdjustments.AsNoTracking()
            .CountAsync(a => a.OrganizationId == organizationId && a.Status == StockDocumentStatus.PendingApproval
                             && bodegas.Contains(a.WarehouseId), ct);
    }

    public async Task<IReadOnlyList<ValuationRowDto>> GetValuationAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, CancellationToken ct = default)
    {
        var filas = await (from c in db.StockItemCosts.AsNoTracking()
                           join w in Bodegas(organizationId, locationId, warehouseId) on c.WarehouseId equals w.Id
                           join i in db.StockItems.AsNoTracking() on c.StockItemId equals i.Id
                           where c.QuantityOnHand > 0
                           orderby w.Name, i.Name
                           select new { w, i, c.QuantityOnHand, c.AverageCost })
            .ToListAsync(ct);
        return filas.Select(x => new ValuationRowDto(
            x.w.Id, x.w.Name, x.i.Id, x.i.Code, x.i.Name, x.i.Kind.ToString(), x.i.BaseUnit, x.QuantityOnHand, x.AverageCost,
            Math.Round(x.QuantityOnHand * x.AverageCost, 2, MidpointRounding.AwayFromZero))).ToList();
    }

    public async Task<IReadOnlyList<MovementReportRowDto>> GetMovementsAsync(
        Guid organizationId, Guid? locationId, Guid? warehouseId, Guid? stockItemId, StockMovementType? type,
        DateTimeOffset fromInclusive, DateTimeOffset toExclusive, int limit, CancellationToken ct = default)
    {
        var q = from m in db.StockMovements.AsNoTracking()
                join w in Bodegas(organizationId, locationId, warehouseId) on m.WarehouseId equals w.Id
                join i in db.StockItems.AsNoTracking() on m.StockItemId equals i.Id
                join l in db.StockLots.AsNoTracking() on m.StockLotId equals l.Id
                where m.OccurredAt >= fromInclusive && m.OccurredAt < toExclusive
                select new { m, w.Name, i.Code, ItemName = i.Name, i.BaseUnit, l.LotCode };
        if (stockItemId is { } item)
        {
            q = q.Where(x => x.m.StockItemId == item);
        }
        if (type is { } t)
        {
            q = q.Where(x => x.m.Type == t);
        }
        var filas = await q.OrderBy(x => x.m.OccurredAt).ThenBy(x => x.m.Id).Take(limit).ToListAsync(ct);
        return filas.Select(x => new MovementReportRowDto(
            x.m.Id, x.m.OccurredAt, x.Name, x.Code, x.ItemName, x.BaseUnit, x.LotCode, x.m.Type.ToString(), x.m.Direction,
            x.m.Quantity, x.m.UnitCost, x.m.TotalCost, x.m.SourceKind, x.m.SourceId)).ToList();
    }

    public async Task<IReadOnlyList<ConsumptionReportRowDto>> GetConsumptionAsync(
        Guid organizationId, Guid? locationId, DateTimeOffset fromInclusive, DateTimeOffset toExclusive,
        CancellationToken ct = default)
    {
        var filas = await (from c in db.InternalConsumptions.AsNoTracking()
                           join w in Bodegas(organizationId, locationId, null) on c.WarehouseId equals w.Id
                           where c.ConsumedAt >= fromInclusive && c.ConsumedAt < toExclusive
                           from l in c.Lines
                           join i in db.StockItems.AsNoTracking() on l.StockItemId equals i.Id
                           select new { c.Area, Bodega = w.Name, i.Id, i.Code, i.Name, i.BaseUnit, l.Quantity, Costo = l.Quantity * l.UnitCost })
            .ToListAsync(ct);
        return filas
            .GroupBy(x => (x.Area, x.Bodega, x.Id, x.Code, x.Name, x.BaseUnit))
            .Select(g => new ConsumptionReportRowDto(
                g.Key.Area, g.Key.Bodega, g.Key.Id, g.Key.Code, g.Key.Name, g.Key.BaseUnit,
                g.Sum(x => x.Quantity), Math.Round(g.Sum(x => x.Costo), 2, MidpointRounding.AwayFromZero)))
            .OrderBy(r => r.Area).ThenByDescending(r => r.Cost)
            .ToList();
    }

    public async Task<DocumentLookup> GetLookupAsync(
        Guid organizationId, IReadOnlyCollection<Guid> itemIds, IReadOnlyCollection<Guid> lotIds,
        IReadOnlyCollection<Guid> warehouseIds, CancellationToken ct = default)
    {
        var items = await db.StockItems.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && itemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Code, i.Name, i.BaseUnit })
            .ToListAsync(ct);
        var lotes = await db.StockLots.AsNoTracking()
            .Where(l => l.OrganizationId == organizationId && lotIds.Contains(l.Id))
            .Select(l => new { l.Id, l.LotCode, l.ExpiryDate })
            .ToListAsync(ct);
        var bodegas = await db.Warehouses.AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && warehouseIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Name })
            .ToListAsync(ct);
        return new DocumentLookup(
            items.ToDictionary(i => i.Id, i => (i.Code, i.Name, i.BaseUnit)),
            lotes.ToDictionary(l => l.Id, l => (l.LotCode, l.ExpiryDate)),
            bodegas.ToDictionary(w => w.Id, w => w.Name));
    }
}
