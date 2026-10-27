using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPricePendingAndAccountOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billable_item_org_lab_panel",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ix_billable_item_org_lab_test",
                table: "billing_billable_item");

            migrationBuilder.AddColumn<bool>(
                name: "price_pending",
                table: "billing_charge",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "consultation_type",
                table: "billing_billable_item",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "price_pending",
                table: "billing_billable_item",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "specialty_code",
                table: "billing_billable_item",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "origin",
                table: "billing_account",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<string>(
                name: "origin_ref",
                table: "billing_account",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Las cuentas abiertas por un encuentro pasan a declarar su origen: así la apertura
            // automática (D2) las encuentra por encuentro o por origen indistintamente.
            migrationBuilder.Sql("""
                UPDATE billing_account
                   SET origin = 'Encounter', origin_ref = encounter_fhir_id
                 WHERE encounter_fhir_id IS NOT NULL;
                """);

            // Los índices de prueba y panel pasan a ser únicos. Si una instalación tiene dos
            // cobrables del mismo examen (no debería: no había pantalla para enlazarlos), queda
            // enlazado el más antiguo y los demás se desenlazan; siguen en el catálogo como
            // servicios sueltos, sin perder historial.
            migrationBuilder.Sql("""
                UPDATE billing_billable_item b
                   SET lab_test_id = NULL
                 WHERE lab_test_id IS NOT NULL
                   AND EXISTS (SELECT 1 FROM billing_billable_item o
                                WHERE o.organization_id = b.organization_id
                                  AND o.lab_test_id = b.lab_test_id
                                  AND (o.created_at, o.id) < (b.created_at, b.id));
                UPDATE billing_billable_item b
                   SET lab_panel_id = NULL
                 WHERE lab_panel_id IS NOT NULL
                   AND EXISTS (SELECT 1 FROM billing_billable_item o
                                WHERE o.organization_id = b.organization_id
                                  AND o.lab_panel_id = b.lab_panel_id
                                  AND (o.created_at, o.id) < (b.created_at, b.id));
                """);

            migrationBuilder.CreateIndex(
                name: "ix_billable_item_org_price_pending",
                table: "billing_billable_item",
                column: "organization_id",
                filter: "price_pending");

            migrationBuilder.CreateIndex(
                name: "ux_billable_item_org_lab_panel",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "lab_panel_id" },
                unique: true,
                filter: "lab_panel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_billable_item_org_lab_test",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "lab_test_id" },
                unique: true,
                filter: "lab_test_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_billable_item_org_specialty_type",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "specialty_code", "consultation_type" },
                unique: true,
                filter: "specialty_code IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_billable_item_org_stock_item",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "stock_item_id" },
                unique: true,
                filter: "stock_item_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_org_origin",
                table: "billing_account",
                columns: new[] { "organization_id", "origin", "origin_ref" },
                unique: true,
                filter: "origin_ref IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_billable_item_org_price_pending",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ux_billable_item_org_lab_panel",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ux_billable_item_org_lab_test",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ux_billable_item_org_specialty_type",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ux_billable_item_org_stock_item",
                table: "billing_billable_item");

            migrationBuilder.DropIndex(
                name: "ux_billing_account_org_origin",
                table: "billing_account");

            migrationBuilder.DropColumn(
                name: "price_pending",
                table: "billing_charge");

            migrationBuilder.DropColumn(
                name: "consultation_type",
                table: "billing_billable_item");

            migrationBuilder.DropColumn(
                name: "price_pending",
                table: "billing_billable_item");

            migrationBuilder.DropColumn(
                name: "specialty_code",
                table: "billing_billable_item");

            migrationBuilder.DropColumn(
                name: "origin",
                table: "billing_account");

            migrationBuilder.DropColumn(
                name: "origin_ref",
                table: "billing_account");

            migrationBuilder.CreateIndex(
                name: "ix_billable_item_org_lab_panel",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "lab_panel_id" },
                filter: "lab_panel_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billable_item_org_lab_test",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "lab_test_id" },
                filter: "lab_test_id IS NOT NULL");
        }
    }
}
