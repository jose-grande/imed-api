using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// FAC-9: todo usuario tiene sede. Hasta acá, un usuario sin asignaciones veía todas las sedes
    /// de su organización; desde ahora la sede decide con qué razón social se factura lo que
    /// atiende, así que nadie queda sin una. Los usuarios existentes sin sede pasan a la sede
    /// principal, que es donde de hecho operaban. Solo datos: el esquema no cambia.
    /// </summary>
    public partial class AssignUsersToLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Usuarios conocidos de cada organización: los que tienen plan asignado (todos los que
            // entraron por alta o invitación aceptada) y los que tienen Practitioner.
            migrationBuilder.Sql("""
                INSERT INTO user_location (organization_id, user_id, location_id, is_primary, assigned_at, assigned_by)
                SELECT u.organization_id, u.user_id, l.id, true, now(), NULL
                  FROM (SELECT organization_id, keycloak_user_id AS user_id FROM user_tier_assignments
                        UNION
                        SELECT organization_id, keycloak_user_id FROM user_practitioner_assignments) u
                  JOIN organization_location l ON l.organization_id = u.organization_id AND l.is_default
                 WHERE NOT EXISTS (
                        SELECT 1 FROM user_location x
                         WHERE x.organization_id = u.organization_id AND x.user_id = u.user_id);
                """);

            // Quien ya tenía sedes pero ninguna principal: la más antigua pasa a serlo.
            migrationBuilder.Sql("""
                UPDATE user_location ul
                   SET is_primary = true
                  FROM (SELECT DISTINCT ON (organization_id, user_id) organization_id, user_id, location_id
                          FROM user_location
                         ORDER BY organization_id, user_id, assigned_at) primera
                 WHERE ul.organization_id = primera.organization_id
                   AND ul.user_id = primera.user_id
                   AND ul.location_id = primera.location_id
                   AND NOT EXISTS (
                        SELECT 1 FROM user_location p
                         WHERE p.organization_id = ul.organization_id AND p.user_id = ul.user_id AND p.is_primary);
                """);

            // Proyección inicial a HAPI, una sola vez: las sedes existentes nunca tuvieron su
            // Location, y los PractitionerRole existentes no tienen sedes. De acá en adelante cada
            // cambio encola lo suyo. Los payloads van en PascalCase porque así los serializa y los
            // lee System.Text.Json con sus opciones por defecto.
            migrationBuilder.Sql("""
                INSERT INTO outbox_events (id, aggregate, type, payload, created_at, attempt_count, next_attempt_at)
                SELECT gen_random_uuid(), 'Location:' || l.id, 'location.fhir-sync',
                       jsonb_build_object('OrganizationId', l.organization_id, 'LocationId', l.id, 'LegalEntityId', NULL),
                       now(), 0, now()
                  FROM organization_location l
                 WHERE l.location_fhir_id IS NULL;
                """);

            migrationBuilder.Sql("""
                INSERT INTO outbox_events (id, aggregate, type, payload, created_at, attempt_count, next_attempt_at)
                SELECT gen_random_uuid(), 'User:' || p.keycloak_user_id || ':Org:' || p.organization_id,
                       'practitioner-role.locations-sync',
                       jsonb_build_object('OrganizationId', p.organization_id, 'UserId', p.keycloak_user_id),
                       now(), 0, now()
                  FROM user_practitioner_assignments p
                 WHERE p.practitioner_role_fhir_id IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin vuelta atrás a propósito: no se puede distinguir una asignación hecha por esta
            // migración de una que después confirmó un administrador.
        }
    }
}
