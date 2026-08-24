using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationAxis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "order_intake",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "lab_order_checkin",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "location_id",
                table: "collection_plan",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "organization_location",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    location_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_location", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "user_location",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    location_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_location", x => new { x.organization_id, x.user_id, x.location_id });
                });

            migrationBuilder.CreateIndex(
                name: "ux_organization_location_org_code",
                table: "organization_location",
                columns: new[] { "organization_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_organization_location_org_default",
                table: "organization_location",
                column: "organization_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ix_user_location_location",
                table: "user_location",
                column: "location_id");

            migrationBuilder.CreateIndex(
                name: "ix_user_location_org_user",
                table: "user_location",
                columns: new[] { "organization_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_location");

            migrationBuilder.DropTable(
                name: "user_location");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "order_intake");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "lab_order_checkin");

            migrationBuilder.DropColumn(
                name: "location_id",
                table: "collection_plan");
        }
    }
}
