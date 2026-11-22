using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IMed.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SarDocumentCodesAndBuyerExemption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "buyer_exempt_purchase_order",
                table: "fiscal_document",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "buyer_exempt_registry",
                table: "fiscal_document",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "buyer_sag_registry",
                table: "fiscal_document",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            // Códigos de tipo del Reglamento del Régimen de Facturación (Acuerdo 481-2017):
            // Nota de Crédito = 07 (art. 25), Nota de Débito = 08 (art. 27). Antes eran 03 y 02, y
            // el SAR autoriza el rango con su código. Solo se corrige un tipo sin documentos
            // emitidos: si los hubiera, esos números ya existen y cambiarle el código al tipo no
            // los arregla — es un caso para el contador, no para una migración.
            migrationBuilder.Sql("""
                UPDATE fiscal_document_type t
                   SET document_code = CASE t.code WHEN 'nota_credito' THEN '07' WHEN 'nota_debito' THEN '08' END,
                       updated_at = now()
                 WHERE t.code IN ('nota_credito', 'nota_debito')
                   AND t.document_code IN ('03', '02')
                   AND NOT EXISTS (SELECT 1 FROM fiscal_document d WHERE d.document_type_id = t.id);
                """);

            // La proforma no es documento fiscal del SAR, y su código 04 es el del Recibo por
            // Honorarios Profesionales. Se desactiva (no se borra: puede tener referencias).
            migrationBuilder.Sql("""
                UPDATE fiscal_document_type
                   SET active = false, updated_at = now()
                 WHERE code = 'proforma' AND active;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "buyer_exempt_purchase_order",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "buyer_exempt_registry",
                table: "fiscal_document");

            migrationBuilder.DropColumn(
                name: "buyer_sag_registry",
                table: "fiscal_document");
        }
    }
}
