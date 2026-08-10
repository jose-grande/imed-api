using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOrderIntakeExamCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "exam_loinc_code",
                table: "order_intake",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "exam_text",
                table: "order_intake",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "exam_loinc_code",
                table: "order_intake");

            migrationBuilder.DropColumn(
                name: "exam_text",
                table: "order_intake");
        }
    }
}
