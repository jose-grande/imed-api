using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabOrderCheckIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_order_checkin",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requisition = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    checked_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    checked_in_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_order_checkin", x => new { x.organization_id, x.requisition });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_order_checkin");
        }
    }
}
