using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddValidationRejection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "result_validation_rejection",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    report_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    service_request_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    reason_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    rejected_by = table.Column<Guid>(type: "uuid", nullable: false),
                    rejected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    resolved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_result_validation_rejection", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "validation_rejection_reason",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    requires_new_sample = table.Column<bool>(type: "boolean", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validation_rejection_reason", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_result_validation_rejection_report",
                table: "result_validation_rejection",
                columns: new[] { "organization_id", "report_fhir_id", "resolved_at" });

            migrationBuilder.CreateIndex(
                name: "ux_validation_rejection_reason_org_code",
                table: "validation_rejection_reason",
                columns: new[] { "organization_id", "code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "result_validation_rejection");

            migrationBuilder.DropTable(
                name: "validation_rejection_reason");
        }
    }
}
