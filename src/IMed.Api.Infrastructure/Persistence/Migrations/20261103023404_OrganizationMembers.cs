using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrganizationMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organization_member",
                columns: table => new
                {
                    keycloak_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_member", x => new { x.keycloak_user_id, x.organization_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_organization_member_org",
                table: "organization_member",
                column: "organization_id");

            // Observación de staging: el administrador no veía a los demás usuarios ni sus sedes,
            // porque la pantalla Equipo partía de los Practitioner de HAPI. Desde ahora la
            // pertenencia es esta tabla, que se llena al iniciar sesión (POST /api/me/session).
            //
            // Llenado inicial con todo lo que ya vincula un usuario con una organización, para que
            // nadie que hoy aparece deje de aparecer: perfiles clínicos, el plan asignado al
            // fundador en el bootstrap (que no tiene Practitioner), sedes asignadas e invitaciones
            // aceptadas (de donde sale el correo). El nombre se completa en el próximo ingreso.
            // Las cuentas creadas desde la consola de Keycloak aparecen en su primer ingreso.
            migrationBuilder.Sql("""
                INSERT INTO organization_member
                    (keycloak_user_id, organization_id, display_name, email, status, joined_at, last_seen_at, updated_at)
                SELECT u.user_id, u.org_id, NULL,
                       (SELECT i.email FROM invitations i
                         WHERE i.keycloak_user_id = u.user_id AND i.organization_id = u.org_id
                           AND i.status = 'Accepted'
                         ORDER BY i.accepted_at DESC NULLS LAST LIMIT 1),
                       'Active', MIN(u.since), NULL, now()
                  FROM (
                      SELECT keycloak_user_id AS user_id, organization_id AS org_id, created_at AS since
                        FROM user_practitioner_assignments
                      UNION ALL
                      SELECT keycloak_user_id, organization_id, assigned_at
                        FROM user_tier_assignments WHERE effective_until IS NULL
                      UNION ALL
                      SELECT user_id, organization_id, assigned_at FROM user_location
                      UNION ALL
                      SELECT keycloak_user_id, organization_id, COALESCE(accepted_at, created_at)
                        FROM invitations WHERE status = 'Accepted' AND keycloak_user_id IS NOT NULL
                  ) u
                 GROUP BY u.user_id, u.org_id
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "organization_member");
        }
    }
}
