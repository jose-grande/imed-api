using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IMed.Api.Infrastructure.Inventory;

/// <summary>
/// Inventario y farmacia sobre EF Core. Lecturas estrictas por organización. Los <c>Add</c> y
/// <c>Update</c> solo rastrean: guarda el handler (o la transacción del inventario), para que un
/// documento y sus movimientos viajen en el mismo <c>SaveChanges</c>.
/// </summary>
public sealed class InventoryRepository(AppDbContext db) : IInventoryRepository
{
    private const string UniqueViolation = "23505";

    // ── Configuración ──

    public async Task<InventorySettings> GetSettingsAsync(Guid organizationId, CancellationToken ct = default) =>
        await db.InventorySettings.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct)
        ?? InventorySettings.Default(organizationId);

    public Task SaveSettingsAsync(InventorySettings settings, CancellationToken ct = default)
    {
        if (db.Entry(settings).State == EntityState.Detached)
        {
            db.InventorySettings.Add(settings);
        }
        return Task.CompletedTask;
    }

    // ── Artículos ──

    public Task<StockItem?> GetItemAsync(Guid organizationId, Guid itemId, CancellationToken ct = default) =>
        db.StockItems.FirstOrDefaultAsync(i => i.OrganizationId == organizationId && i.Id == itemId, ct);

    public async Task<IReadOnlyList<StockItem>> GetItemsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.StockItems.Where(i => i.OrganizationId == organizationId && ids.Contains(i.Id)).ToListAsync(ct);

    public Task<bool> ItemCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.StockItems.AnyAsync(i => i.OrganizationId == organizationId && i.Code == c, ct);
    }

    public async Task<IReadOnlyList<StockItem>> SearchItemsAsync(
        Guid organizationId, string? term, StockItemKind? kind, bool? overTheCounter, bool activeOnly, int limit,
        CancellationToken ct = default)
    {
        var q = db.StockItems.AsNoTracking().Where(i => i.OrganizationId == organizationId);
        if (activeOnly)
        {
            q = q.Where(i => i.Active);
        }
        if (kind is { } k)
        {
            q = q.Where(i => i.Kind == k);
        }
        if (overTheCounter is { } otc)
        {
            q = q.Where(i => i.OverTheCounter == otc);
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            var patron = $"%{t}%";
            var codigo = t.ToUpperInvariant();
            q = q.Where(i => EF.Functions.ILike(i.Name, patron)
                             || i.Code == codigo
                             || EF.Functions.ILike(i.Code, patron)
                             || i.Barcode == t
                             || (i.Medication != null && i.Medication.AtcCode == codigo)
                             || (i.Medication != null && i.Medication.ActiveIngredient != null
                                 && EF.Functions.ILike(i.Medication.ActiveIngredient, patron)));
        }
        return await q.OrderBy(i => i.Name).Take(limit).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StockItem>> FindMedicationsByAtcAsync(
        Guid organizationId, string atcCode, CancellationToken ct = default)
    {
        var atc = atcCode.Trim().ToUpperInvariant();
        return await db.StockItems.AsNoTracking()
            .Where(i => i.OrganizationId == organizationId && i.Active && i.Kind == StockItemKind.Medication
                        && i.Medication != null && i.Medication.AtcCode == atc)
            .OrderBy(i => i.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StockItem>> ListSellableWithoutBillableAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.StockItems
            .Where(i => i.OrganizationId == organizationId && i.Sellable && i.BillableItemId == null && i.TaxCodeId != null)
            .ToListAsync(ct);

    public Task AddItemAsync(StockItem item, CancellationToken ct = default)
    {
        db.StockItems.Add(item);
        return Task.CompletedTask;
    }

    public Task UpdateItemAsync(StockItem item, CancellationToken ct = default)
    {
        if (db.Entry(item).State == EntityState.Detached)
        {
            db.StockItems.Update(item);
        }
        return Task.CompletedTask;
    }

    // ── Bodegas ──

    public Task<Warehouse?> GetWarehouseAsync(Guid organizationId, Guid warehouseId, CancellationToken ct = default) =>
        db.Warehouses.FirstOrDefaultAsync(w => w.OrganizationId == organizationId && w.Id == warehouseId, ct);

    public async Task<IReadOnlyList<Warehouse>> ListWarehousesAsync(
        Guid organizationId, Guid? locationId, bool activeOnly, CancellationToken ct = default)
    {
        var q = db.Warehouses.Where(w => w.OrganizationId == organizationId);
        if (locationId is { } l)
        {
            q = q.Where(w => w.LocationId == l);
        }
        if (activeOnly)
        {
            q = q.Where(w => w.Active);
        }
        return await q.OrderBy(w => w.Name).ToListAsync(ct);
    }

    public Task<bool> WarehouseCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.Warehouses.AnyAsync(w => w.OrganizationId == organizationId && w.Code == c, ct);
    }

    public Task AddWarehouseAsync(Warehouse warehouse, CancellationToken ct = default)
    {
        db.Warehouses.Add(warehouse);
        return Task.CompletedTask;
    }

    public async Task<bool> TryAddGeneralWarehouseAsync(Warehouse warehouse, CancellationToken ct = default)
    {
        db.Warehouses.Add(warehouse);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(warehouse).State = EntityState.Detached;
            return false;
        }
    }

    public Task UpdateWarehouseAsync(Warehouse warehouse, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<IReadOnlyList<Guid>> ListOrganizationsWithLocationsAsync(CancellationToken ct = default) =>
        await db.OrganizationLocations.AsNoTracking().Select(l => l.OrganizationId).Distinct().ToListAsync(ct);

    // ── Proveedores ──

    public Task<Supplier?> GetSupplierAsync(Guid organizationId, Guid supplierId, CancellationToken ct = default) =>
        db.Suppliers.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.Id == supplierId, ct);

    public async Task<IReadOnlyList<Supplier>> ListSuppliersAsync(
        Guid organizationId, string? term, bool activeOnly, CancellationToken ct = default)
    {
        var q = db.Suppliers.AsNoTracking().Where(s => s.OrganizationId == organizationId);
        if (activeOnly)
        {
            q = q.Where(s => s.Active);
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var patron = $"%{term.Trim()}%";
            q = q.Where(s => EF.Functions.ILike(s.Name, patron) || (s.TaxId != null && EF.Functions.ILike(s.TaxId, patron)));
        }
        return await q.OrderBy(s => s.Name).Take(500).ToListAsync(ct);
    }

    public Task<bool> SupplierTaxIdExistsAsync(Guid organizationId, string taxId, Guid? exceptId, CancellationToken ct = default) =>
        db.Suppliers.AnyAsync(s => s.OrganizationId == organizationId && s.TaxId == taxId && s.Id != exceptId, ct);

    public Task AddSupplierAsync(Supplier supplier, CancellationToken ct = default)
    {
        db.Suppliers.Add(supplier);
        return Task.CompletedTask;
    }

    public Task UpdateSupplierAsync(Supplier supplier, CancellationToken ct = default) => Task.CompletedTask;

    // ── Unidades ──

    public async Task<IReadOnlyList<InventoryUnit>> ListUnitsAsync(Guid organizationId, CancellationToken ct = default) =>
        await db.InventoryUnits.AsNoTracking().Where(u => u.OrganizationId == organizationId)
            .OrderBy(u => u.Name).ToListAsync(ct);

    public Task<InventoryUnit?> GetUnitAsync(Guid organizationId, Guid unitId, CancellationToken ct = default) =>
        db.InventoryUnits.FirstOrDefaultAsync(u => u.OrganizationId == organizationId && u.Id == unitId, ct);

    public Task<InventoryUnit?> FindUnitByNameAsync(Guid organizationId, string name, CancellationToken ct = default) =>
        db.InventoryUnits.AsNoTracking().FirstOrDefaultAsync(u => u.OrganizationId == organizationId && u.Name == name, ct);

    public async Task<bool> TryAddUnitAsync(InventoryUnit unit, CancellationToken ct = default)
    {
        db.InventoryUnits.Add(unit);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(unit).State = EntityState.Detached;
            return false;
        }
    }

    public async Task<IReadOnlyList<string>> ListItemBaseUnitsAsync(Guid organizationId, CancellationToken ct = default) =>
        await db.StockItems.AsNoTracking().Where(i => i.OrganizationId == organizationId)
            .Select(i => i.BaseUnit).Distinct().ToListAsync(ct);

    public Task<int> RenameItemsBaseUnitAsync(Guid organizationId, string oldName, string newName, CancellationToken ct = default) =>
        db.StockItems.Where(i => i.OrganizationId == organizationId && i.BaseUnit == oldName)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.BaseUnit, newName), ct);

    // ── Lotes ──

    public Task<StockLot?> GetLotAsync(Guid organizationId, Guid lotId, CancellationToken ct = default) =>
        db.StockLots.FirstOrDefaultAsync(l => l.OrganizationId == organizationId && l.Id == lotId, ct);

    public async Task<IReadOnlyList<StockLot>> GetLotsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> ids, CancellationToken ct = default) =>
        await db.StockLots.Where(l => l.OrganizationId == organizationId && ids.Contains(l.Id)).ToListAsync(ct);

    public Task<StockLot?> FindLotAsync(Guid stockItemId, string lotCode, CancellationToken ct = default)
    {
        var c = lotCode.Trim().ToUpperInvariant();
        return db.StockLots.FirstOrDefaultAsync(l => l.StockItemId == stockItemId && l.LotCode == c, ct);
    }

    public async Task<StockLot> GetOrCreateLotAsync(
        Guid organizationId, Guid stockItemId, string lotCode, DateOnly? expiryDate, CancellationToken ct = default)
    {
        if (await FindLotAsync(stockItemId, lotCode, ct) is { } existente)
        {
            return existente;
        }
        return await TryAddLotAsync(StockLot.Create(organizationId, stockItemId, lotCode, expiryDate), ct)
            ?? await FindLotAsync(stockItemId, lotCode, ct)
            ?? throw new InvalidOperationException("No se pudo crear ni encontrar el lote.");
    }

    public async Task<StockLot> GetOrCreateUntrackedLotAsync(Guid organizationId, Guid stockItemId, CancellationToken ct = default)
    {
        if (await FindLotAsync(stockItemId, StockLot.UntrackedCode, ct) is { } existente)
        {
            return existente;
        }
        return await TryAddLotAsync(StockLot.CreateUntracked(organizationId, stockItemId), ct)
            ?? await FindLotAsync(stockItemId, StockLot.UntrackedCode, ct)
            ?? throw new InvalidOperationException("No se pudo crear ni encontrar el lote.");
    }

    /// <summary>
    /// Inserta el lote solo. Dos entradas del mismo lote a la vez chocan en el índice único; la que
    /// pierde lee la del ganador. Dentro de una transacción, EF guarda detrás de un savepoint y el
    /// choque no la envenena.
    /// </summary>
    private async Task<StockLot?> TryAddLotAsync(StockLot lote, CancellationToken ct)
    {
        db.StockLots.Add(lote);
        try
        {
            await db.SaveChangesAsync(ct);
            return lote;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            db.Entry(lote).State = EntityState.Detached;
            return null;
        }
    }

    public Task UpdateLotAsync(StockLot lot, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<IReadOnlyList<LotAvailability>> GetAvailabilityAsync(
        Guid warehouseId, Guid stockItemId, CancellationToken ct = default) =>
        await (from b in db.StockBalances.AsNoTracking()
               join l in db.StockLots.AsNoTracking() on b.StockLotId equals l.Id
               where b.WarehouseId == warehouseId && b.StockItemId == stockItemId && b.QuantityOnHand > 0
               select new LotAvailability(l.Id, l.LotCode, l.ExpiryDate, b.QuantityOnHand, l.Status, l.IsUntracked))
            .ToListAsync(ct);

    // ── Documentos ──

    public Task AddReceiptAsync(GoodsReceipt receipt, CancellationToken ct = default)
    {
        db.GoodsReceipts.Add(receipt);
        return Task.CompletedTask;
    }

    public Task<GoodsReceipt?> GetReceiptAsync(Guid organizationId, Guid receiptId, CancellationToken ct = default) =>
        db.GoodsReceipts.FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.Id == receiptId, ct);

    public Task<bool> ReceiptExistsAsync(
        Guid organizationId, Guid supplierId, string supplierInvoiceNumber, CancellationToken ct = default) =>
        db.GoodsReceipts.AnyAsync(r => r.OrganizationId == organizationId && r.SupplierId == supplierId
                                       && r.SupplierInvoiceNumber == supplierInvoiceNumber, ct);

    public Task AddTransferAsync(StockTransfer transfer, CancellationToken ct = default)
    {
        db.StockTransfers.Add(transfer);
        return Task.CompletedTask;
    }

    public Task<StockTransfer?> GetTransferAsync(Guid organizationId, Guid transferId, CancellationToken ct = default) =>
        db.StockTransfers.FirstOrDefaultAsync(t => t.OrganizationId == organizationId && t.Id == transferId, ct);

    public Task AddConsumptionAsync(InternalConsumption consumption, CancellationToken ct = default)
    {
        db.InternalConsumptions.Add(consumption);
        return Task.CompletedTask;
    }

    public Task<InternalConsumption?> GetConsumptionAsync(Guid organizationId, Guid consumptionId, CancellationToken ct = default) =>
        db.InternalConsumptions.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == consumptionId, ct);

    public Task AddAdjustmentAsync(StockAdjustment adjustment, CancellationToken ct = default)
    {
        if (db.Entry(adjustment).State == EntityState.Detached)
        {
            db.StockAdjustments.Add(adjustment);
        }
        return Task.CompletedTask;
    }

    public Task<StockAdjustment?> GetAdjustmentAsync(Guid organizationId, Guid adjustmentId, CancellationToken ct = default) =>
        db.StockAdjustments.FirstOrDefaultAsync(a => a.OrganizationId == organizationId && a.Id == adjustmentId, ct);

    public Task AddCountAsync(StockCount count, CancellationToken ct = default)
    {
        db.StockCounts.Add(count);
        return Task.CompletedTask;
    }

    public Task<StockCount?> GetCountAsync(Guid organizationId, Guid countId, CancellationToken ct = default) =>
        db.StockCounts.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.Id == countId, ct);

    public async Task<IReadOnlyList<(Guid StockItemId, Guid StockLotId, decimal Quantity)>> GetWarehouseBalancesAsync(
        Guid warehouseId, CancellationToken ct = default) =>
        (await db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId)
            .Select(b => new { b.StockItemId, b.StockLotId, b.QuantityOnHand })
            .ToListAsync(ct))
        .Select(b => (b.StockItemId, b.StockLotId, b.QuantityOnHand))
        .ToList();

    public async Task<decimal> GetLotBalanceAsync(Guid warehouseId, Guid stockLotId, CancellationToken ct = default) =>
        await db.StockBalances.AsNoTracking()
            .Where(b => b.WarehouseId == warehouseId && b.StockLotId == stockLotId)
            .Select(b => (decimal?)b.QuantityOnHand)
            .FirstOrDefaultAsync(ct) ?? 0m;

    // ── Farmacia ──

    public Task AddDispenseAsync(PharmacyDispense dispense, CancellationToken ct = default)
    {
        db.PharmacyDispenses.Add(dispense);
        return Task.CompletedTask;
    }

    public Task<PharmacyDispense?> GetDispenseAsync(Guid organizationId, Guid dispenseId, CancellationToken ct = default) =>
        db.PharmacyDispenses.FirstOrDefaultAsync(d => d.OrganizationId == organizationId && d.Id == dispenseId, ct);

    public Task<PharmacyDispense?> FindDispenseByKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken ct = default) =>
        db.PharmacyDispenses.FirstOrDefaultAsync(d => d.OrganizationId == organizationId && d.IdempotencyKey == idempotencyKey, ct);

    public Task<PharmacyDispense?> FindDispenseByLineAsync(Guid organizationId, Guid lineId, CancellationToken ct = default) =>
        db.PharmacyDispenses.FirstOrDefaultAsync(
            d => d.OrganizationId == organizationId && d.Lines.Any(l => l.Id == lineId), ct);

    public async Task<IReadOnlyDictionary<string, (decimal Dispensed, bool Completed)>> GetPrescriptionFillAsync(
        Guid organizationId, IReadOnlyCollection<string> medicationRequestIds, CancellationToken ct = default)
    {
        if (medicationRequestIds.Count == 0)
        {
            return new Dictionary<string, (decimal, bool)>();
        }
        var filas = await db.PharmacyDispenses.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.Status == PharmacyDispenseStatus.Completed)
            .SelectMany(d => d.Lines)
            .Where(l => l.MedicationRequestFhirId != null && medicationRequestIds.Contains(l.MedicationRequestFhirId))
            .Select(l => new { l.MedicationRequestFhirId, l.Quantity, l.CompletesPrescription })
            .ToListAsync(ct);
        return filas
            .GroupBy(f => f.MedicationRequestFhirId!)
            .ToDictionary(g => g.Key, g => (g.Sum(x => x.Quantity), g.Any(x => x.CompletesPrescription)), StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<PharmacyDispense>> ListDispensesAsync(
        Guid organizationId, Guid? locationId, PharmacyDispenseKind? kind, DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive, int limit, CancellationToken ct = default)
    {
        var q = db.PharmacyDispenses.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId && d.DispensedAt >= fromInclusive && d.DispensedAt < toExclusive);
        if (locationId is { } l)
        {
            q = q.Where(d => d.LocationId == l);
        }
        if (kind is { } k)
        {
            q = q.Where(d => d.Kind == k);
        }
        return await q.OrderByDescending(d => d.DispensedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
    }

    public Task<bool> ReturnRecordExistsAsync(Guid organizationId, Guid creditNoteId, Guid chargeId, CancellationToken ct = default) =>
        db.StockReturnRecords.AnyAsync(
            r => r.OrganizationId == organizationId && r.CreditNoteId == creditNoteId && r.ChargeId == chargeId, ct);

    public Task AddReturnRecordAsync(StockReturnRecord record, CancellationToken ct = default)
    {
        db.StockReturnRecords.Add(record);
        return Task.CompletedTask;
    }
}
