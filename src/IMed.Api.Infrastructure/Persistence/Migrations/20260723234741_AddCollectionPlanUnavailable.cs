using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionPlanUnavailable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "collection_plan_unavailable_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_plan_id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_request_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    loinc_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    display = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    reason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_plan_unavailable_item", x => x.id);
                    table.ForeignKey(
                        name: "FK_collection_plan_unavailable_item_collection_plan_collection~",
                        column: x => x.collection_plan_id,
                        principalTable: "collection_plan",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_plan_unavailable_plan",
                table: "collection_plan_unavailable_item",
                column: "collection_plan_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_plan_unavailable_item");
        }
    }
}
