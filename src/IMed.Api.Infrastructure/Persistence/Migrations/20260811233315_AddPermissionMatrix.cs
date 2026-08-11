using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPermissionMatrix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "permission",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    module = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permission", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "policy_version",
                columns: table => new
                {
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_version", x => x.organization_id);
                });

            migrationBuilder.CreateTable(
                name: "role_permission",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: true),
                    role_code = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    permission_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    scope = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    denied = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permission", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_role_permission_role",
                table: "role_permission",
                column: "role_code");

            migrationBuilder.CreateIndex(
                name: "ux_role_permission_org_role_perm",
                table: "role_permission",
                columns: new[] { "organization_id", "role_code", "permission_code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "permission");

            migrationBuilder.DropTable(
                name: "policy_version");

            migrationBuilder.DropTable(
                name: "role_permission");
        }
    }
}
