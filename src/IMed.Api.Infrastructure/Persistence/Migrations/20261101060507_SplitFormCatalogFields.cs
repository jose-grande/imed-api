using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SplitFormCatalogFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "form_catalog_fields",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    catalog_id = table.Column<Guid>(type: "uuid", nullable: false),
                    field_key = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    widget = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    core = table.Column<bool>(type: "boolean", nullable: false),
                    custom = table.Column<bool>(type: "boolean", nullable: false),
                    email = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    editable = table.Column<string>(type: "jsonb", nullable: false),
                    defaults = table.Column<string>(type: "jsonb", nullable: false),
                    messages = table.Column<string>(type: "jsonb", nullable: true),
                    list_source = table.Column<string>(type: "jsonb", nullable: true),
                    binding = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_form_catalog_fields", x => x.id);
                    table.ForeignKey(
                        name: "FK_form_catalog_fields_form_catalogs_catalog_id",
                        column: x => x.catalog_id,
                        principalTable: "form_catalogs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_form_catalog_fields_catalog_key",
                table: "form_catalog_fields",
                columns: new[] { "catalog_id", "field_key" },
                unique: true);

            // Antes de borrar la columna, cada campo del arreglo jsonb pasa a su propia fila, para no
            // perder los catálogos ya cargados. Un valor que no sea un arreglo se ignora.
            migrationBuilder.Sql(
                """
                INSERT INTO form_catalog_fields (
                    id, catalog_id, field_key, sort_order, widget, core, custom, email, active,
                    editable, defaults, messages, list_source, binding, created_at, updated_at)
                SELECT
                    gen_random_uuid(), c.id, f.elem ->> 'key', (f.ord)::int, f.elem ->> 'widget',
                    COALESCE((f.elem ->> 'core')::boolean, FALSE),
                    COALESCE((f.elem ->> 'custom')::boolean, FALSE),
                    COALESCE((f.elem ->> 'email')::boolean, FALSE),
                    TRUE,
                    COALESCE(f.elem -> 'editable', '[]'::jsonb),
                    COALESCE(f.elem -> 'defaults', '{}'::jsonb),
                    f.elem -> 'messages',
                    f.elem -> 'list',
                    COALESCE(f.elem -> 'binding', '{}'::jsonb),
                    now(), now()
                FROM form_catalogs c
                CROSS JOIN LATERAL jsonb_array_elements(c.fields) WITH ORDINALITY AS f(elem, ord)
                WHERE jsonb_typeof(c.fields) = 'array'
                  AND f.elem ? 'key'
                  AND f.elem ? 'widget';
                """);

            migrationBuilder.DropColumn(
                name: "fields",
                table: "form_catalogs");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "fields",
                table: "form_catalogs",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            // Se rearma el arreglo de campos desde las filas (activas, en su orden).
            migrationBuilder.Sql(
                """
                UPDATE form_catalogs c
                SET fields = COALESCE((
                    SELECT jsonb_agg(
                        jsonb_strip_nulls(jsonb_build_object(
                            'key', f.field_key,
                            'widget', f.widget,
                            'core', NULLIF(f.core, FALSE),
                            'custom', NULLIF(f.custom, FALSE),
                            'email', NULLIF(f.email, FALSE),
                            'defaults', f.defaults,
                            'editable', f.editable,
                            'messages', f.messages,
                            'list', f.list_source,
                            'binding', f.binding))
                        ORDER BY f.sort_order)
                    FROM form_catalog_fields f
                    WHERE f.catalog_id = c.id AND f.active), '[]'::jsonb);
                """);

            migrationBuilder.DropTable(
                name: "form_catalog_fields");
        }
    }
}
