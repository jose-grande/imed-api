using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "collected_at",
                table: "collection_plan_container",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "received_at",
                table: "collection_plan_container",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason_code",
                table: "collection_plan_container",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "collection_plan_container",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "storage_location",
                table: "collection_plan_container",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "collection_event",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    collection_plan_container_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    by = table.Column<Guid>(type: "uuid", nullable: true),
                    detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_event", x => x.id);
                    table.ForeignKey(
                        name: "FK_collection_event_collection_plan_container_collection_plan_~",
                        column: x => x.collection_plan_container_id,
                        principalTable: "collection_plan_container",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_collection_event_container",
                table: "collection_event",
                column: "collection_plan_container_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "collection_event");

            migrationBuilder.DropColumn(
                name: "collected_at",
                table: "collection_plan_container");

            migrationBuilder.DropColumn(
                name: "received_at",
                table: "collection_plan_container");

            migrationBuilder.DropColumn(
                name: "rejection_reason_code",
                table: "collection_plan_container");

            migrationBuilder.DropColumn(
                name: "status",
                table: "collection_plan_container");

            migrationBuilder.DropColumn(
                name: "storage_location",
                table: "collection_plan_container");
        }
    }
}
