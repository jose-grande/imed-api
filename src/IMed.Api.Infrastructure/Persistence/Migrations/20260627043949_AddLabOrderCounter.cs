using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLabOrderCounter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lab_order_counter",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    day = table.Column<DateOnly>(type: "date", nullable: false),
                    seq = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lab_order_counter", x => new { x.organization_id, x.day });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lab_order_counter");
        }
    }
}
