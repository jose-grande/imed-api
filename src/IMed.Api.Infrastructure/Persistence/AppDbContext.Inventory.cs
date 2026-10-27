using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Inventario y farmacia (plan de salida a producción, Stream B). El mapeo vive en
/// <c>Persistence/Configurations/Inventory/</c>; aquí solo los conjuntos.
/// </summary>
public sealed partial class AppDbContext
{
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<InventorySettings> InventorySettings => Set<InventorySettings>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockItemCost> StockItemCosts => Set<StockItemCost>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<InternalConsumption> InternalConsumptions => Set<InternalConsumption>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();
    public DbSet<StockCount> StockCounts => Set<StockCount>();
    public DbSet<PharmacyDispense> PharmacyDispenses => Set<PharmacyDispense>();
    public DbSet<StockReturnRecord> StockReturnRecords => Set<StockReturnRecord>();
}
