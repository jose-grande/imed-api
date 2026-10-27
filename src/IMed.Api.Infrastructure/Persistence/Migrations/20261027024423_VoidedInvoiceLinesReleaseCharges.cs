using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class VoidedInvoiceLinesReleaseCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoice_line_org_charge",
                table: "billing_invoice_line");

            migrationBuilder.AddColumn<bool>(
                name: "voided",
                table: "billing_invoice_line",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Lo anulado antes de este cambio: sus líneas quedan marcadas y sus cargos vuelven a
            // pendientes de facturar, salvo que ya estén en otra factura vigente.
            migrationBuilder.Sql("""
                UPDATE billing_invoice_line l
                   SET voided = true
                  FROM billing_invoice i
                 WHERE i.id = l.invoice_id AND i.status = 'Voided';

                UPDATE billing_charge c
                   SET status = 'Captured', updated_at = now()
                 WHERE c.status = 'Invoiced'
                   AND EXISTS (SELECT 1 FROM billing_invoice_line l
                                WHERE l.charge_id = c.id AND l.voided)
                   AND NOT EXISTS (SELECT 1 FROM billing_invoice_line l
                                    WHERE l.charge_id = c.id AND NOT l.voided);
                """);

            migrationBuilder.CreateIndex(
                name: "ux_invoice_line_org_charge",
                table: "billing_invoice_line",
                columns: new[] { "organization_id", "charge_id" },
                unique: true,
                filter: "NOT voided");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_invoice_line_org_charge",
                table: "billing_invoice_line");

            migrationBuilder.DropColumn(
                name: "voided",
                table: "billing_invoice_line");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_line_org_charge",
                table: "billing_invoice_line",
                columns: new[] { "organization_id", "charge_id" },
                unique: true);
        }
    }
}
