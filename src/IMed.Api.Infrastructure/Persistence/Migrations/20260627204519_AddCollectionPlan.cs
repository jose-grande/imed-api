using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_plan",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requisition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    encounter_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    patient_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    computed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_plan", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "collection_plan_container",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    specimen_type_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    specimen_type_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    tube_type_code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    tube_type_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    additive = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    required_volume_ml = table.Column<decimal>(type: "numeric(7,2)", precision: 7, scale: 2, nullable: false),
                    accession_number = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    barcode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    specimen_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_plan_container", x => x.id);
                    table.ForeignKey(
                        name: "FK_collection_plan_container_collection_plan_collection_plan_id",
                        column: x => x.collection_plan_id,
                        principalTable: "collection_plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collection_plan_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_plan_container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    lab_test_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    lab_test_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_plan_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_collection_plan_item_collection_plan_container_collection_p~",
                        column: x => x.collection_plan_container_id,
                        principalTable: "collection_plan_container",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_collection_plan_org_requisition",
                table: "collection_plan",
                columns: new[] { "organization_id", "requisition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_collection_plan_container_plan",
                table: "collection_plan_container",
                column: "collection_plan_id");

            migrationBuilder.CreateIndex(
                name: "ix_collection_plan_item_container",
                table: "collection_plan_item",
                column: "collection_plan_container_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_plan_item");

            migrationBuilder.DropTable(
                name: "collection_plan_container");

            migrationBuilder.DropTable(
                name: "collection_plan");
        }
    }
}
