using IMed.Api.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IMed.Api.Infrastructure.Persistence.Configurations.Inventory;

// Mapeo del inventario (plan de salida a producción, B1; FSF §8). Convenciones del documento
// rector: cantidades en numeric(18,6) —mL, fracciones de ampolla—, dinero en numeric(19,4) y costos
// unitarios en numeric(19,6) para no perder el costo de una tableta al dividir el de una caja.
// Los nombres de tabla y columna pasan después por la convención snake_case del contexto, así que
// los filtros de índice se escriben ya en snake_case.

internal static class InventoryPrecision
{
    public static PropertyBuilder<decimal> Quantity(this PropertyBuilder<decimal> p) => p.HasPrecision(18, 6);
    public static PropertyBuilder<decimal?> Quantity(this PropertyBuilder<decimal?> p) => p.HasPrecision(18, 6);
    public static PropertyBuilder<decimal> Money(this PropertyBuilder<decimal> p) => p.HasPrecision(19, 4);
    public static PropertyBuilder<decimal> UnitCost(this PropertyBuilder<decimal> p) => p.HasPrecision(19, 6);
}

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> e)
    {
        e.ToTable("stock_item");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Code).HasMaxLength(StockItem.CodeMaxLength).IsRequired();
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Barcode).HasMaxLength(64);
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.BaseUnit).HasMaxLength(30).IsRequired();
        e.Property(x => x.RequiresLot).IsRequired();
        e.Property(x => x.Sellable).IsRequired();
        e.Property(x => x.OverTheCounter).IsRequired();
        e.Property(x => x.TaxCodeId);
        e.Property(x => x.BillableItemId);
        e.Property(x => x.MinimumStock).Quantity();
        e.Property(x => x.Active).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        // Perfiles 1:1 en tablas propias (FSF §8): en vez de cuarenta columnas vacías en el maestro.
        e.OwnsOne(x => x.Medication, m =>
        {
            m.ToTable("stock_item_medication");
            m.WithOwner().HasForeignKey("StockItemId");
            m.Property(p => p.AtcCode).HasMaxLength(10).IsRequired();
            m.Property(p => p.ActiveIngredient).HasMaxLength(200);
            m.Property(p => p.Concentration).HasMaxLength(100);
            m.Property(p => p.DoseForm).HasMaxLength(60);
            m.Property(p => p.Route).HasMaxLength(60);
            m.Property(p => p.SanitaryRegistration).HasMaxLength(60);
            // La receta se despacha buscando los artículos de su código ATC (P10).
            m.HasIndex(p => p.AtcCode).HasDatabaseName("ix_stock_item_medication_atc");
        });
        e.OwnsOne(x => x.Reagent, r =>
        {
            r.ToTable("stock_item_reagent");
            r.WithOwner().HasForeignKey("StockItemId");
            r.Property(p => p.StorageConditions).HasMaxLength(200);
        });
        e.OwnsOne(x => x.Supply, s =>
        {
            s.ToTable("stock_item_supply");
            s.WithOwner().HasForeignKey("StockItemId");
        });

        e.OwnsMany(x => x.Presentations, p =>
        {
            p.ToTable("stock_item_presentation");
            p.WithOwner().HasForeignKey(x => x.StockItemId);
            p.HasKey(x => x.Id);
            p.Property(x => x.Id).ValueGeneratedNever();
            p.Property(x => x.Name).HasMaxLength(60).IsRequired();
            p.Property(x => x.FactorToBase).Quantity().IsRequired();
        });
        e.Navigation(x => x.Presentations).UsePropertyAccessMode(PropertyAccessMode.Field);

        e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_stock_item_org_code");
        e.HasIndex(x => new { x.OrganizationId, x.Barcode })
            .HasFilter("barcode IS NOT NULL")
            .HasDatabaseName("ix_stock_item_org_barcode");
        e.HasIndex(x => new { x.OrganizationId, x.Active, x.Name }).HasDatabaseName("ix_stock_item_org_active_name");
    }
}

internal sealed class WarehouseConfiguration : IEntityTypeConfiguration<Warehouse>
{
    public void Configure(EntityTypeBuilder<Warehouse> e)
    {
        e.ToTable("stock_warehouse");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.LocationId).IsRequired();
        e.Property(x => x.Code).HasMaxLength(40).IsRequired();
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.IsGeneral).IsRequired();
        e.Property(x => x.Active).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        e.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique().HasDatabaseName("ux_stock_warehouse_org_code");

        // P7: una sola bodega general por sede, aunque el backfill y una petición la creen a la vez.
        e.HasIndex(x => x.LocationId)
            .IsUnique()
            .HasFilter("is_general")
            .HasDatabaseName("ux_stock_warehouse_general_per_location");
        e.HasIndex(x => new { x.OrganizationId, x.LocationId }).HasDatabaseName("ix_stock_warehouse_org_location");
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> e)
    {
        e.ToTable("stock_supplier");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.TaxId).HasMaxLength(32);
        e.Property(x => x.ContactName).HasMaxLength(200);
        e.Property(x => x.Phone).HasMaxLength(40);
        e.Property(x => x.Email).HasMaxLength(200);
        e.Property(x => x.Active).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        e.HasIndex(x => new { x.OrganizationId, x.Name }).HasDatabaseName("ix_stock_supplier_org_name");
        // Un RTN es un proveedor: dos fichas del mismo contribuyente parten su historial de compras.
        e.HasIndex(x => new { x.OrganizationId, x.TaxId })
            .IsUnique()
            .HasFilter("tax_id IS NOT NULL")
            .HasDatabaseName("ux_stock_supplier_org_tax_id");
    }
}

internal sealed class InventorySettingsConfiguration : IEntityTypeConfiguration<InventorySettings>
{
    public void Configure(EntityTypeBuilder<InventorySettings> e)
    {
        e.ToTable("inventory_settings");
        e.HasKey(x => x.OrganizationId);
        e.Property(x => x.OrganizationId).ValueGeneratedNever();
        e.Property(x => x.AdjustmentApprovalThreshold).Money().IsRequired();
        e.Property(x => x.ExpiryAlertDays).IsRequired();
        e.Property(x => x.WalkInPartyId);
        e.Property(x => x.UpdatedAt).IsRequired();
    }
}

internal sealed class StockLotConfiguration : IEntityTypeConfiguration<StockLot>
{
    public void Configure(EntityTypeBuilder<StockLot> e)
    {
        e.ToTable("stock_lot");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.StockItemId).IsRequired();
        e.Property(x => x.LotCode).HasMaxLength(60).IsRequired();
        e.Property(x => x.ExpiryDate);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.IsUntracked).IsRequired();
        e.Property(x => x.CreatedAt).IsRequired();

        e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);

        // Un lote del fabricante es uno solo por artículo: la segunda compra del mismo lote suma
        // al existente en vez de partir su historia (y su retiro) en dos.
        e.HasIndex(x => new { x.StockItemId, x.LotCode }).IsUnique().HasDatabaseName("ux_stock_lot_item_code");
        e.HasIndex(x => new { x.OrganizationId, x.ExpiryDate }).HasDatabaseName("ix_stock_lot_org_expiry");
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> e)
    {
        e.ToTable("stock_movement");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.GroupId).IsRequired();
        e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        e.Property(x => x.OccurredAt).IsRequired();
        e.Property(x => x.Direction).IsRequired();
        e.Property(x => x.Quantity).Quantity().IsRequired();
        e.Property(x => x.UnitCost).UnitCost().IsRequired();
        e.Property(x => x.TotalCost).Money().IsRequired();
        e.Property(x => x.QuantityAfter).Quantity().IsRequired();
        e.Property(x => x.AverageCostAfter).UnitCost().IsRequired();
        e.Property(x => x.SourceKind).HasMaxLength(40).IsRequired();
        e.Property(x => x.SourceId).IsRequired();
        e.Property(x => x.SourceLineId);
        e.Property(x => x.PerformedBy);
        e.Property(x => x.ReversalOfMovementId);
        e.Property(x => x.Notes).HasMaxLength(500);

        e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);

        e.ToTable(t =>
        {
            t.HasCheckConstraint("ck_stock_movement_direction", "direction IN (1, -1)");
            t.HasCheckConstraint("ck_stock_movement_quantity", "quantity > 0");
        });

        // Kardex: los movimientos de un artículo en una bodega, en orden.
        e.HasIndex(x => new { x.WarehouseId, x.StockItemId, x.OccurredAt }).HasDatabaseName("ix_stock_movement_kardex");
        // Reportes por rango de fechas.
        e.HasIndex(x => new { x.OrganizationId, x.OccurredAt }).HasDatabaseName("ix_stock_movement_org_date");
        e.HasIndex(x => new { x.SourceKind, x.SourceId }).HasDatabaseName("ix_stock_movement_source");
    }
}

internal sealed class StockBalanceConfiguration : IEntityTypeConfiguration<StockBalance>
{
    public void Configure(EntityTypeBuilder<StockBalance> e)
    {
        e.ToTable("stock_balance", t =>
            // P6: ninguna bodega queda en negativo. El código lo valida antes; esto es la red.
            t.HasCheckConstraint("ck_stock_balance_non_negative", "quantity_on_hand >= 0"));
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.QuantityOnHand).Quantity().IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);

        e.HasIndex(x => new { x.WarehouseId, x.StockLotId }).IsUnique().HasDatabaseName("ux_stock_balance_warehouse_lot");
        e.HasIndex(x => new { x.OrganizationId, x.StockItemId }).HasDatabaseName("ix_stock_balance_org_item");
    }
}

internal sealed class StockItemCostConfiguration : IEntityTypeConfiguration<StockItemCost>
{
    public void Configure(EntityTypeBuilder<StockItemCost> e)
    {
        e.ToTable("stock_item_cost", t =>
            t.HasCheckConstraint("ck_stock_item_cost_non_negative", "quantity_on_hand >= 0 AND average_cost >= 0"));
        e.HasKey(x => new { x.WarehouseId, x.StockItemId });
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.QuantityOnHand).Quantity().IsRequired();
        e.Property(x => x.AverageCost).UnitCost().IsRequired();
        e.Property(x => x.UpdatedAt).IsRequired();

        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        e.HasIndex(x => x.OrganizationId).HasDatabaseName("ix_stock_item_cost_org");
    }
}

internal sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> e)
    {
        e.ToTable("stock_goods_receipt");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.SupplierInvoiceNumber).HasMaxLength(60).IsRequired();
        e.Property(x => x.SupplierInvoiceDate).IsRequired();
        e.Property(x => x.ReceivedAt).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.Property(x => x.Total).Money().IsRequired();
        e.Property(x => x.Voided).IsRequired();
        e.Property(x => x.VoidReason).HasMaxLength(500);

        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);

        e.OwnsMany(x => x.Lines, l =>
        {
            l.ToTable("stock_goods_receipt_line");
            l.WithOwner().HasForeignKey(x => x.GoodsReceiptId);
            l.HasKey(x => x.Id);
            l.Property(x => x.Id).ValueGeneratedNever();
            l.Property(x => x.PresentationName).HasMaxLength(60);
            l.Property(x => x.PresentationQuantity).Quantity().IsRequired();
            l.Property(x => x.FactorToBase).Quantity().IsRequired();
            l.Property(x => x.Quantity).Quantity().IsRequired();
            l.Property(x => x.UnitCost).UnitCost().IsRequired();
            l.Property(x => x.LineTotal).Money().IsRequired();
            l.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);
        });
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // La misma factura del mismo proveedor entra una vez (también vuelve idempotente un doble clic).
        e.HasIndex(x => new { x.OrganizationId, x.SupplierId, x.SupplierInvoiceNumber })
            .IsUnique()
            .HasDatabaseName("ux_stock_goods_receipt_supplier_invoice");
        e.HasIndex(x => new { x.OrganizationId, x.ReceivedAt }).HasDatabaseName("ix_stock_goods_receipt_org_date");
    }
}

/// <summary>Líneas de traslado, consumo y ajuste: misma forma, tabla propia por documento.</summary>
internal static class StockDocumentLineMapping
{
    public static void Map<TOwner>(OwnedNavigationBuilder<TOwner, StockDocumentLine> l, string table)
        where TOwner : class
    {
        l.ToTable(table, t => t.HasCheckConstraint($"ck_{table}_quantity", "quantity > 0"));
        l.WithOwner().HasForeignKey(x => x.DocumentId);
        l.HasKey(x => x.Id);
        l.Property(x => x.Id).ValueGeneratedNever();
        l.Property(x => x.Quantity).Quantity().IsRequired();
        l.Property(x => x.UnitCost).UnitCost().IsRequired();
        l.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
        l.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class StockTransferConfiguration : IEntityTypeConfiguration<StockTransfer>
{
    public void Configure(EntityTypeBuilder<StockTransfer> e)
    {
        e.ToTable("stock_transfer");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.TransferredAt).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.OwnsMany(x => x.Lines, l => StockDocumentLineMapping.Map(l, "stock_transfer_line"));
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        e.HasIndex(x => new { x.OrganizationId, x.TransferredAt }).HasDatabaseName("ix_stock_transfer_org_date");
    }
}

internal sealed class InternalConsumptionConfiguration : IEntityTypeConfiguration<InternalConsumption>
{
    public void Configure(EntityTypeBuilder<InternalConsumption> e)
    {
        e.ToTable("stock_internal_consumption");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Area).HasMaxLength(120).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.Property(x => x.ConsumedAt).IsRequired();
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.OwnsMany(x => x.Lines, l => StockDocumentLineMapping.Map(l, "stock_internal_consumption_line"));
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        e.HasIndex(x => new { x.OrganizationId, x.ConsumedAt }).HasDatabaseName("ix_stock_internal_consumption_org_date");
    }
}

internal sealed class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> e)
    {
        e.ToTable("stock_adjustment");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(30).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.Value).Money().IsRequired();
        e.Property(x => x.RequestedAt).IsRequired();
        e.Property(x => x.RejectionReason).HasMaxLength(500);
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.OwnsMany(x => x.Lines, l => StockDocumentLineMapping.Map(l, "stock_adjustment_line"));
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        e.HasIndex(x => new { x.OrganizationId, x.Status }).HasDatabaseName("ix_stock_adjustment_org_status");
    }
}

internal sealed class StockCountConfiguration : IEntityTypeConfiguration<StockCount>
{
    public void Configure(EntityTypeBuilder<StockCount> e)
    {
        e.ToTable("stock_count");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.Property(x => x.StartedAt).IsRequired();
        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        e.OwnsMany(x => x.Lines, l =>
        {
            l.ToTable("stock_count_line");
            l.WithOwner().HasForeignKey(x => x.StockCountId);
            l.HasKey(x => x.Id);
            l.Property(x => x.Id).ValueGeneratedNever();
            l.Property(x => x.ExpectedQuantity).Quantity().IsRequired();
            l.Property(x => x.CountedQuantity).Quantity();
            l.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);
        });
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Un conteo abierto por bodega: dos conteos a la vez generarían dos ajustes por la misma diferencia.
        e.HasIndex(x => x.WarehouseId)
            .IsUnique()
            .HasFilter("status = 'Draft'")
            .HasDatabaseName("ux_stock_count_open_per_warehouse");
    }
}
