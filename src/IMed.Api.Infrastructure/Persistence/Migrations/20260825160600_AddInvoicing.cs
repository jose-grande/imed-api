using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "issuer_legal_name",
                table: "fiscal_regime",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuer_tax_id",
                table: "fiscal_regime",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "billing_invoice",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_to_party_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_to_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issue_date = table.Column<DateOnly>(type: "date", nullable: false),
                    due_date = table.Column<DateOnly>(type: "date", nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    balance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    fiscal_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_invoice", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_document",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    fiscal_authorization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    establishment_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    emission_point_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    document_type_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    full_number = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    issuer_tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    buyer_party_id = table.Column<Guid>(type: "uuid", nullable: true),
                    buyer_tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    buyer_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    subtotal_exempt = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    subtotal_exonerated = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    subtotal_taxed15 = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    subtotal_taxed18 = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    grand_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    amount_in_words = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    cai_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    range_snapshot = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    limit_date_snapshot = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    void_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    replaces_document_id = table.Column<Guid>(type: "uuid", nullable: true),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_document", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "billing_invoice_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invoice_id = table.Column<Guid>(type: "uuid", nullable: false),
                    charge_id = table.Column<Guid>(type: "uuid", nullable: false),
                    description_snapshot = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    tax_code_snapshot = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    tax_treatment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_invoice_line", x => x.id);
                    table.ForeignKey(
                        name: "FK_billing_invoice_line_billing_invoice_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "billing_invoice",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_org_account",
                table: "billing_invoice",
                columns: new[] { "organization_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_invoice_org_status",
                table: "billing_invoice",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_billing_invoice_line_invoice_id",
                table: "billing_invoice_line",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "ux_invoice_line_org_charge",
                table: "billing_invoice_line",
                columns: new[] { "organization_id", "charge_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_document_org_number",
                table: "fiscal_document",
                columns: new[] { "organization_id", "full_number" });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_document_invoice",
                table: "fiscal_document",
                column: "invoice_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_document_number",
                table: "fiscal_document",
                columns: new[] { "organization_id", "establishment_code", "emission_point_code", "document_type_code", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_invoice_line");

            migrationBuilder.DropTable(
                name: "fiscal_document");

            migrationBuilder.DropTable(
                name: "billing_invoice");

            migrationBuilder.DropColumn(
                name: "issuer_legal_name",
                table: "fiscal_regime");

            migrationBuilder.DropColumn(
                name: "issuer_tax_id",
                table: "fiscal_regime");
        }
    }
}
