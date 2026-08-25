using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_billable_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tax_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    payment_timing = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    lab_test_id = table.Column<Guid>(type: "uuid", nullable: true),
                    lab_panel_id = table.Column<Guid>(type: "uuid", nullable: true),
                    stock_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    service_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_billable_item", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_price_list",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    payer_contract_id = table.Column<Guid>(type: "uuid", nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_price_list", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_price_list_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billable_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    min_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    discount_pct_max = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_price_list_item", x => x.id);
                });

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

            migrationBuilder.CreateIndex(
                name: "ux_billable_item_org_code",
                table: "billing_billable_item",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_price_list_org_code",
                table: "billing_price_list",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_list_item_list",
                table: "billing_price_list_item",
                column: "price_list_id");

            migrationBuilder.CreateIndex(
                name: "ix_price_list_item_org_item",
                table: "billing_price_list_item",
                columns: new[] { "organization_id", "billable_item_id" });

            migrationBuilder.CreateIndex(
                name: "ux_price_list_item_list_item_from",
                table: "billing_price_list_item",
                columns: new[] { "price_list_id", "billable_item_id", "valid_from" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_billable_item");

            migrationBuilder.DropTable(
                name: "billing_price_list");

            migrationBuilder.DropTable(
                name: "billing_price_list_item");
        }
    }
}
