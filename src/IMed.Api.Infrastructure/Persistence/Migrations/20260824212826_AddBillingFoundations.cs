using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBillingFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fiscal_establishment_code",
                table: "organization_location",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "party",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    patient_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    tax_id_kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_party", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_authorization_limit",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    value = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    requires_approval_above = table.Column<bool>(type: "boolean", nullable: false),
                    valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    valid_to = table.Column<DateOnly>(type: "date", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_authorization_limit", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_party_org_kind",
                table: "party",
                columns: new[] { "organization_id", "kind" });

            migrationBuilder.CreateIndex(
                name: "ix_party_org_tax_id",
                table: "party",
                columns: new[] { "organization_id", "tax_id" });

            migrationBuilder.CreateIndex(
                name: "ux_party_org_patient",
                table: "party",
                columns: new[] { "organization_id", "patient_fhir_id" },
                unique: true,
                filter: "patient_fhir_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_user_authorization_limit_org_user_kind",
                table: "user_authorization_limit",
                columns: new[] { "organization_id", "user_id", "kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "party");

            migrationBuilder.DropTable(
                name: "user_authorization_limit");

            migrationBuilder.DropColumn(
                name: "fiscal_establishment_code",
                table: "organization_location");
        }
    }
}
