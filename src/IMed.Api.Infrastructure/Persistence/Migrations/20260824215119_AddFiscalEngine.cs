using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFiscalEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cash_point",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    profile = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    emission_point_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    is_temporary = table.Column<bool>(type: "boolean", nullable: false),
                    printer_profile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cash_point", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_authorization",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cai = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    emission_point_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    range_from = table.Column<long>(type: "bigint", nullable: false),
                    range_to = table.Column<long>(type: "bigint", nullable: false),
                    next_sequence = table.Column<long>(type: "bigint", nullable: false),
                    limit_date = table.Column<DateOnly>(type: "date", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_authorization", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_document_type",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    document_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    affects_receivable = table.Column<bool>(type: "boolean", nullable: false),
                    sign = table.Column<short>(type: "smallint", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_document_type", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "fiscal_regime",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    regime_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    config_json = table.Column<string>(type: "jsonb", nullable: true),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fiscal_regime", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tax_code",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    rate = table.Column<decimal>(type: "numeric(7,4)", precision: 7, scale: 4, nullable: false),
                    treatment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_code", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_cash_point_org_location",
                table: "cash_point",
                columns: new[] { "organization_id", "location_id" });

            migrationBuilder.CreateIndex(
                name: "ux_cash_point_org_code",
                table: "cash_point",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_authorization_org_location_type_status",
                table: "fiscal_authorization",
                columns: new[] { "organization_id", "location_id", "document_type_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_authorization_org_cai_type_point",
                table: "fiscal_authorization",
                columns: new[] { "organization_id", "cai", "document_type_id", "emission_point_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_document_type_org_code",
                table: "fiscal_document_type",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_fiscal_regime_org_from",
                table: "fiscal_regime",
                columns: new[] { "organization_id", "valid_from" });

            migrationBuilder.CreateIndex(
                name: "ux_tax_code_org_code",
                table: "tax_code",
                columns: new[] { "organization_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cash_point");

            migrationBuilder.DropTable(
                name: "fiscal_authorization");

            migrationBuilder.DropTable(
                name: "fiscal_document_type");

            migrationBuilder.DropTable(
                name: "fiscal_regime");

            migrationBuilder.DropTable(
                name: "tax_code");
        }
    }
}
