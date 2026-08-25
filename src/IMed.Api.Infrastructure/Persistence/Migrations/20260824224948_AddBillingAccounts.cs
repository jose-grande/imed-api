using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    patient_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    guarantor_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    encounter_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_charge",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    billable_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    price_source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    price_list_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tax_code_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    description_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_authorized_by = table.Column<Guid>(type: "uuid", nullable: true),
                    component = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    performed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    performer_practitioner_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source_kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    source_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    source_fhir_ref = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reversed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    reversal_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_charge", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_billing_account_org_patient",
                table: "billing_account",
                columns: new[] { "organization_id", "patient_fhir_id" });

            migrationBuilder.CreateIndex(
                name: "ix_billing_account_org_status",
                table: "billing_account",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_billing_account_org_encounter",
                table: "billing_account",
                columns: new[] { "organization_id", "encounter_fhir_id" },
                unique: true,
                filter: "encounter_fhir_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_billing_charge_account_status",
                table: "billing_charge",
                columns: new[] { "account_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_billing_charge_source_idempotency",
                table: "billing_charge",
                columns: new[] { "organization_id", "source_kind", "source_id", "billable_item_id", "component" },
                unique: true,
                filter: "status <> 'Reversed'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_account");

            migrationBuilder.DropTable(
                name: "billing_charge");
        }
    }
}
