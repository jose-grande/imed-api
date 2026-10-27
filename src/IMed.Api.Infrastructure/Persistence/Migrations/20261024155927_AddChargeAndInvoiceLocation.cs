using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddChargeAndInvoiceLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "legal_entity_id",
                table: "billing_invoice",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "billing_invoice",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "billing_charge",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // ── Backfill (FAC-9, D3): la sede de cada cargo existente ──
            // 1. Lo ya facturado: la sede que lo emitió. Es la respuesta que ya quedó en papel.
            migrationBuilder.Sql("""
                UPDATE billing_charge c
                   SET location_id = d.location_id
                  FROM billing_invoice_line il
                  JOIN fiscal_document d ON d.invoice_id = il.invoice_id
                 WHERE il.charge_id = c.id
                   AND c.location_id = '00000000-0000-0000-0000-000000000000';
                """);

            // 2. Lo del laboratorio: la sede donde se admitió la orden.
            migrationBuilder.Sql("""
                UPDATE billing_charge c
                   SET location_id = oi.location_id
                  FROM order_intake oi
                 WHERE c.source_kind = 'LabOrder'
                   AND oi.organization_id = c.organization_id
                   AND oi.service_request_fhir_id = c.source_id
                   AND oi.location_id IS NOT NULL
                   AND c.location_id = '00000000-0000-0000-0000-000000000000';
                """);

            // 3. El resto se prestó cuando la organización operaba en un solo lugar: la principal.
            migrationBuilder.Sql("""
                UPDATE billing_charge c
                   SET location_id = l.id
                  FROM organization_location l
                 WHERE l.organization_id = c.organization_id
                   AND l.is_default
                   AND c.location_id = '00000000-0000-0000-0000-000000000000';
                """);

            migrationBuilder.Sql("ALTER TABLE billing_charge ALTER COLUMN location_id DROP DEFAULT;");

            // Facturas emitidas: la sede y la razón social de su documento fiscal.
            migrationBuilder.Sql("""
                UPDATE billing_invoice i
                   SET location_id = d.location_id,
                       legal_entity_id = COALESCE(d.legal_entity_id, l.legal_entity_id)
                  FROM fiscal_document d
                  JOIN organization_location l ON l.id = d.location_id
                 WHERE d.invoice_id = i.id
                   AND i.location_id IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "legal_entity_id",
                table: "billing_invoice");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "billing_invoice");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "billing_charge");
        }
    }
}
