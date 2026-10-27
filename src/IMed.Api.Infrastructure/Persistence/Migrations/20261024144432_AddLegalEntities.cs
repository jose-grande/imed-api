using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLegalEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_fiscal_authorization_org_cai_type_point",
                table: "fiscal_authorization");

            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "organization_location",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "legal_entity_id",
                table: "organization_location",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "phone",
                table: "organization_location",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "establishment_address",
                table: "fiscal_document",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuer_fiscal_address",
                table: "fiscal_document",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuer_legal_name",
                table: "fiscal_document",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "issuer_trade_name",
                table: "fiscal_document",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "legal_entity_id",
                table: "fiscal_document",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "legal_entity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    legal_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    trade_name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    tax_id = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fiscal_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    email = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    organization_fhir_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_legal_entity", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_organization_location_legal_entity",
                table: "organization_location",
                column: "legal_entity_id");

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_authorization_org_location_cai_type_point",
                table: "fiscal_authorization",
                columns: new[] { "organization_id", "location_id", "cai", "document_type_id", "emission_point_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_legal_entity_org_default",
                table: "legal_entity",
                column: "organization_id",
                unique: true,
                filter: "is_default");

            migrationBuilder.CreateIndex(
                name: "ux_legal_entity_org_tax_id",
                table: "legal_entity",
                columns: new[] { "organization_id", "tax_id" },
                unique: true,
                filter: "tax_id IS NOT NULL");

            // ── Backfill (FAC-9) ──────────────────────────────────────────────────────────
            // Toda organización existente operaba con un solo emisor: su razón social por
            // defecto se arma con el emisor del régimen vigente (la única fuente local de RTN),
            // y todas sus sedes pasan a ella. Así una clínica de un solo RTN no ve ningún cambio.
            migrationBuilder.Sql("""
                INSERT INTO legal_entity
                    (id, organization_id, legal_name, tax_id, is_default, active, created_at, updated_at)
                SELECT gen_random_uuid(),
                       o.organization_id,
                       COALESCE(NULLIF(btrim(r.issuer_legal_name), ''),
                                NULLIF(btrim(s.name), ''),
                                'Razón social principal'),
                       NULLIF(regexp_replace(COALESCE(r.issuer_tax_id, ''), '[- ]', '', 'g'), ''),
                       true, true, now(), now()
                  FROM (SELECT organization_id FROM organization_location
                        UNION
                        SELECT organization_id FROM organization_subscriptions) o
                  LEFT JOIN organization_subscriptions s ON s.organization_id = o.organization_id
                  LEFT JOIN LATERAL (
                        SELECT fr.issuer_legal_name, fr.issuer_tax_id
                          FROM fiscal_regime fr
                         WHERE fr.organization_id = o.organization_id
                         ORDER BY fr.valid_from DESC
                         LIMIT 1) r ON true
                 WHERE NOT EXISTS (
                        SELECT 1 FROM legal_entity le
                         WHERE le.organization_id = o.organization_id AND le.is_default);
                """);

            migrationBuilder.Sql("""
                UPDATE organization_location l
                   SET legal_entity_id = le.id
                  FROM legal_entity le
                 WHERE le.organization_id = l.organization_id
                   AND le.is_default
                   AND l.legal_entity_id = '00000000-0000-0000-0000-000000000000';
                """);

            // La columna nació con default guid-cero solo para poder agregarla NOT NULL sobre
            // filas existentes. Sin quitarlo, una sede insertada sin razón social fallaría por la
            // FK con un mensaje que no dice nada; sin default, falla diciendo qué falta.
            migrationBuilder.Sql("ALTER TABLE organization_location ALTER COLUMN legal_entity_id DROP DEFAULT;");

            // Documentos ya emitidos: se congela el emisor con el que salieron, que es la razón
            // social recién creada a partir del régimen vigente.
            migrationBuilder.Sql("""
                UPDATE fiscal_document d
                   SET legal_entity_id = l.legal_entity_id,
                       issuer_legal_name = COALESCE(d.issuer_legal_name, le.legal_name)
                  FROM organization_location l
                  JOIN legal_entity le ON le.id = l.legal_entity_id
                 WHERE l.id = d.location_id
                   AND d.legal_entity_id IS NULL;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_organization_location_legal_entity_legal_entity_id",
                table: "organization_location",
                column: "legal_entity_id",
                principalTable: "legal_entity",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_organization_location_legal_entity_legal_entity_id",
                table: "organization_location");

            migrationBuilder.DropTable(
                name: "legal_entity");

            migrationBuilder.DropIndex(
                name: "ix_organization_location_legal_entity",
                table: "organization_location");

            migrationBuilder.DropIndex(
                name: "ux_fiscal_authorization_org_location_cai_type_point",
                table: "fiscal_authorization");

            migrationBuilder.DropColumn(
                name: "address",
                table: "organization_location");

            migrationBuilder.DropColumn(
                name: "legal_entity_id",
                table: "organization_location");

            migrationBuilder.DropColumn(
                name: "phone",
                table: "organization_location");

            migrationBuilder.DropColumn(
                name: "establishment_address",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "issuer_fiscal_address",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "issuer_legal_name",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "issuer_trade_name",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "legal_entity_id",
                table: "fiscal_document");

            migrationBuilder.CreateIndex(
                name: "ux_fiscal_authorization_org_cai_type_point",
                table: "fiscal_authorization",
                columns: new[] { "organization_id", "cai", "document_type_id", "emission_point_code" },
                unique: true);
        }
    }
}
