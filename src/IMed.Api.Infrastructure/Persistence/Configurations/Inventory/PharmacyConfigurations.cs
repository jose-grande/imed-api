using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IMed.Api.Infrastructure.Persistence.Configurations.Inventory;

// Farmacia ambulatoria y venta libre (plan de salida a producción, B4–B6; FSF §9.1–9.2). La
// farmacia no tiene inventario propio: opera sobre bodegas, y aquí solo vive el acto de entrega.

internal sealed class PharmacyDispenseConfiguration : IEntityTypeConfiguration<PharmacyDispense>
{
    public void Configure(EntityTypeBuilder<PharmacyDispense> e)
    {
        e.ToTable("pharmacy_dispense");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.IdempotencyKey).HasMaxLength(64).IsRequired();
        e.Property(x => x.LocationId).IsRequired();
        e.Property(x => x.AccountId).IsRequired();
        e.Property(x => x.PatientFhirId).HasMaxLength(64);
        e.Property(x => x.PatientName).HasMaxLength(200);
        e.Property(x => x.EncounterFhirId).HasMaxLength(64);
        e.Property(x => x.BuyerName).HasMaxLength(300);
        e.Property(x => x.BuyerTaxId).HasMaxLength(32);
        e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        e.Property(x => x.DispensedAt).IsRequired();
        e.Property(x => x.Notes).HasMaxLength(500);
        e.Property(x => x.CancellationReason).HasMaxLength(500);

        e.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);

        e.OwnsMany(x => x.Lines, l =>
        {
            l.ToTable("pharmacy_dispense_line", t => t.HasCheckConstraint("ck_pharmacy_dispense_line_quantity", "quantity > 0"));
            l.WithOwner().HasForeignKey(x => x.DispenseId);
            l.HasKey(x => x.Id);
            l.Property(x => x.Id).ValueGeneratedNever();
            l.Property(x => x.Description).HasMaxLength(300).IsRequired();
            l.Property(x => x.Quantity).Quantity().IsRequired();
            l.Property(x => x.MedicationRequestFhirId).HasMaxLength(64);
            l.Property(x => x.PrescribedAtcCode).HasMaxLength(10);
            l.Property(x => x.PrescribedText).HasMaxLength(300);
            l.Property(x => x.PrescribedQuantity).Quantity();
            l.Property(x => x.PrescribedUnit).HasMaxLength(40);
            l.Property(x => x.SubstitutionReason).HasMaxLength(500);
            l.HasOne<StockItem>().WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.Restrict);

            // La cola de recetas pregunta "¿qué se despachó de esta receta?".
            l.HasIndex(x => x.MedicationRequestFhirId).HasDatabaseName("ix_pharmacy_dispense_line_mr");

            l.OwnsMany(x => x.Lots, lot =>
            {
                lot.ToTable("pharmacy_dispense_lot", t => t.HasCheckConstraint(
                    "ck_pharmacy_dispense_lot_returned", "returned_quantity >= 0 AND returned_quantity <= quantity"));
                lot.WithOwner().HasForeignKey(x => x.DispenseLineId);
                lot.HasKey(x => x.Id);
                lot.Property(x => x.Id).ValueGeneratedNever();
                lot.Property(x => x.Quantity).Quantity().IsRequired();
                lot.Property(x => x.UnitCost).UnitCost().IsRequired();
                lot.Property(x => x.ReturnedQuantity).Quantity().IsRequired();
                lot.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId).OnDelete(DeleteBehavior.Restrict);
            });
            l.Navigation(x => x.Lots).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
        e.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // 🔴 Idempotencia del despacho: la misma llave no despacha dos veces.
        e.HasIndex(x => new { x.OrganizationId, x.IdempotencyKey })
            .IsUnique()
            .HasDatabaseName("ux_pharmacy_dispense_org_key");
        e.HasIndex(x => new { x.OrganizationId, x.LocationId, x.DispensedAt })
            .HasDatabaseName("ix_pharmacy_dispense_org_location_date");
        e.HasIndex(x => new { x.OrganizationId, x.EncounterFhirId })
            .HasFilter("encounter_fhir_id IS NOT NULL")
            .HasDatabaseName("ix_pharmacy_dispense_org_encounter");
    }
}

internal sealed class StockReturnRecordConfiguration : IEntityTypeConfiguration<StockReturnRecord>
{
    public void Configure(EntityTypeBuilder<StockReturnRecord> e)
    {
        e.ToTable("stock_return_record");
        e.HasKey(x => x.Id);
        e.Property(x => x.OrganizationId).IsRequired();
        e.Property(x => x.Quantity).Quantity().IsRequired();
        e.Property(x => x.ReturnedQuantity).Quantity().IsRequired();
        e.Property(x => x.Note).HasMaxLength(500);
        e.Property(x => x.ProcessedAt).IsRequired();

        // 🔴 La nota de crédito se puede reintentar: la misma línea no devuelve dos veces.
        e.HasIndex(x => new { x.OrganizationId, x.CreditNoteId, x.ChargeId })
            .IsUnique()
            .HasDatabaseName("ux_stock_return_credit_note_charge");
    }
}
