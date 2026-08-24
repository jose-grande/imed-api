using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabOperational : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_accession_counter",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_accession_counter", x => new { x.organization_id, x.day });
                });

            migrationBuilder.CreateTable(
                name: "order_intake",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    specimen_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    patient_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    routing = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scheduled_for = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    is_walk_in = table.Column<bool>(type: "boolean", nullable: false),
                    fasting_required = table.Column<bool>(type: "boolean", nullable: false),
                    fasting_satisfied = table.Column<bool>(type: "boolean", nullable: true),
                    accession_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    block_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    received_by = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_intake", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "specimen_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_intake_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    incident = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_specimen_event", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_intake_accession",
                table: "order_intake",
                column: "accession_number");

            migrationBuilder.CreateIndex(
                name: "ix_order_intake_org_status",
                table: "order_intake",
                columns: new[] { "organization_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_order_intake_patient",
                table: "order_intake",
                column: "patient_fhir_id");

            migrationBuilder.CreateIndex(
                name: "ix_order_intake_service_request",
                table: "order_intake",
                column: "service_request_fhir_id");

            migrationBuilder.CreateIndex(
                name: "ix_specimen_event_intake",
                table: "specimen_event",
                column: "order_intake_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_accession_counter");

            migrationBuilder.DropTable(
                name: "order_intake");

            migrationBuilder.DropTable(
                name: "specimen_event");
        }
    }
}
