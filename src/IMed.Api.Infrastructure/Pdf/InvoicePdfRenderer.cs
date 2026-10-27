using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// PDF de factura (FAC-5). Dos plantillas sobre el mismo contenido: carta y ticket de 80 mm.
///
/// <para><b>Por qué dos y no una responsiva.</b> No es el mismo documento en distinto ancho: el
/// ticket omite dirección y teléfono del emisor, aprieta la tabla a dos columnas y no imprime
/// línea de firma. Intentar que una sola plantilla haga las dos cosas produce un ticket con
/// espacios en blanco de una carta.</para>
/// </summary>
public sealed class InvoicePdfRenderer : IInvoicePdfRenderer
{
    private static readonly CultureInfo Hn = CultureInfo.GetCultureInfo("es-HN");
    private static readonly Color Ink = new(30, 30, 30);
    private static readonly Color Soft = new(110, 110, 110);
    private static readonly Color Rule = new(210, 210, 210);
    private static readonly Color Stop = new(160, 40, 40);

    public InvoicePdfRenderer()
    {
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(InvoicePdfModel model, InvoicePrintFormat format = InvoicePrintFormat.Letter)
    {
        ArgumentNullException.ThrowIfNull(model);

        var document = format == InvoicePrintFormat.Thermal80
            ? BuildThermal(model)
            : BuildLetter(model);

        var renderer = new PdfDocumentRenderer(unicode: true) { Document = document };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    // ── Carta ───────────────────────────────────────────────────────────────

    private static Document BuildLetter(InvoicePdfModel m)
    {
        var doc = NewDocument(m);
        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.Letter;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.0);

        RenderVoidBanner(section, m);

        // Emisor
        PdfLogoRenderer.TryDraw(section, m.Issuer.Logo);
        section.AddParagraph(m.Issuer.LegalName).Style = "OrgTitle";
        if (!string.IsNullOrWhiteSpace(m.Issuer.TaxId))
        {
            section.AddParagraph($"RTN {m.Issuer.TaxId}").Style = "Subtle";
        }
        if (!string.IsNullOrWhiteSpace(m.Issuer.Address))
        {
            section.AddParagraph(m.Issuer.Address).Style = "Subtle";
        }
        if (!string.IsNullOrWhiteSpace(m.Issuer.Phone))
        {
            section.AddParagraph($"Tel. {m.Issuer.Phone}").Style = "Subtle";
        }

        Divider(section);

        // Documento
        var titulo = section.AddParagraph(m.Fiscal.DocumentTypeName.ToUpperInvariant());
        titulo.Style = "DocTitle";
        var numero = section.AddParagraph(m.Fiscal.FullNumber);
        numero.Style = "Number";

        section.AddParagraph(
            $"Emitida el {m.Fiscal.IssuedAt.ToLocalTime():dd/MM/yyyy HH:mm}").Style = "Subtle";

        if (m.IsReprint)
        {
            // Un duplicado que no se anuncia como tal es indistinguible del original, y con dos
            // papeles idénticos circulando nadie sabe cuál respalda el pago.
            var copia = section.AddParagraph("REIMPRESIÓN — no válida como original");
            copia.Style = "Subtle";
            copia.Format.Font.Bold = true;
        }

        // Comprador
        section.AddParagraph("Cliente").Style = "SectionTitle";
        section.AddParagraph(m.Buyer.Name);
        section.AddParagraph(
            string.IsNullOrWhiteSpace(m.Buyer.TaxId) ? "RTN: consumidor final" : $"RTN {m.Buyer.TaxId}")
            .Style = "Subtle";

        // Detalle
        section.AddParagraph("Detalle").Style = "SectionTitle";
        RenderLinesTable(section, m, compact: false);

        // Totales
        RenderTotals(section, m, compact: false);

        if (!string.IsNullOrWhiteSpace(m.Totals.AmountInWords))
        {
            var letras = section.AddParagraph(m.Totals.AmountInWords);
            letras.Style = "Subtle";
            letras.Format.SpaceBefore = Unit.FromPoint(6);
        }

        // Pie legal
        Divider(section);
        RenderFiscalFooter(section, m);

        return doc;
    }

    // ── Ticket 80 mm ────────────────────────────────────────────────────────

    private static Document BuildThermal(InvoicePdfModel m)
    {
        var doc = NewDocument(m);
        var section = doc.AddSection();

        // 80 mm de ancho, alto libre: la impresora térmica corta donde termina el contenido.
        section.PageSetup.PageWidth = Unit.FromMillimeter(80);
        section.PageSetup.PageHeight = Unit.FromMillimeter(280);
        section.PageSetup.TopMargin = Unit.FromMillimeter(4);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(6);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(4);
        section.PageSetup.RightMargin = Unit.FromMillimeter(4);

        doc.Styles["Normal"]!.Font.Size = 8;

        RenderVoidBanner(section, m);

        // En el ticket el logo va centrado y mucho más chico: el ancho imprimible son 72 mm, y
        // la impresora térmica rasteriza — un logo grande alarga la impresión sin aportar nada.
        PdfLogoRenderer.TryDraw(
            section,
            m.Issuer.Logo,
            ParagraphAlignment.Center,
            maxWidth: Unit.FromMillimeter(40),
            maxHeight: Unit.FromMillimeter(14));

        var org = section.AddParagraph(m.Issuer.LegalName);
        org.Format.Font.Bold = true;
        org.Format.Alignment = ParagraphAlignment.Center;

        if (!string.IsNullOrWhiteSpace(m.Issuer.TaxId))
        {
            Centered(section, $"RTN {m.Issuer.TaxId}", "Subtle");
        }

        Divider(section);

        Centered(section, m.Fiscal.DocumentTypeName.ToUpperInvariant(), bold: true);
        Centered(section, m.Fiscal.FullNumber, bold: true);
        Centered(section, m.Fiscal.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Hn), "Subtle");

        if (m.IsReprint)
        {
            Centered(section, "REIMPRESIÓN", bold: true);
        }

        Divider(section);

        section.AddParagraph(m.Buyer.Name);
        section.AddParagraph(
            string.IsNullOrWhiteSpace(m.Buyer.TaxId) ? "Consumidor final" : $"RTN {m.Buyer.TaxId}")
            .Style = "Subtle";

        Divider(section);
        RenderLinesTable(section, m, compact: true);
        Divider(section);
        RenderTotals(section, m, compact: true);

        if (!string.IsNullOrWhiteSpace(m.Totals.AmountInWords))
        {
            var letras = section.AddParagraph(m.Totals.AmountInWords);
            letras.Style = "Subtle";
            letras.Format.SpaceBefore = Unit.FromPoint(4);
        }

        Divider(section);
        RenderFiscalFooter(section, m);

        return doc;
    }

    // ── Piezas compartidas ──────────────────────────────────────────────────

    private static Document NewDocument(InvoicePdfModel m)
    {
        var doc = new Document
        {
            Info =
            {
                Title = $"{m.Fiscal.DocumentTypeName} {m.Fiscal.FullNumber}",
                Author = m.Issuer.LegalName,
                Subject = $"{m.Fiscal.DocumentTypeName} para {m.Buyer.Name}",
            },
        };
        DefineStyles(doc);
        return doc;
    }

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var org = doc.Styles.AddStyle("OrgTitle", "Normal");
        org.Font.Size = 14;
        org.Font.Bold = true;
        org.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var subtle = doc.Styles.AddStyle("Subtle", "Normal");
        subtle.Font.Size = 8;
        subtle.Font.Color = Soft;

        var section = doc.Styles.AddStyle("SectionTitle", "Normal");
        section.Font.Size = 10;
        section.Font.Bold = true;
        section.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        section.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var docTitle = doc.Styles.AddStyle("DocTitle", "Normal");
        docTitle.Font.Size = 12;
        docTitle.Font.Bold = true;
        docTitle.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        docTitle.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var number = doc.Styles.AddStyle("Number", "Normal");
        number.Font.Size = 13;
        number.Font.Bold = true;
        number.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
    }

    /// <summary>
    /// Lo anulado se marca de forma imposible de pasar por alto. Un documento anulado que se ve
    /// igual que uno vigente circula como si valiera.
    /// </summary>
    private static void RenderVoidBanner(Section section, InvoicePdfModel m)
    {
        if (!m.Fiscal.IsVoided)
        {
            return;
        }

        var banner = section.AddParagraph("DOCUMENTO ANULADO");
        banner.Format.Font.Bold = true;
        banner.Format.Font.Size = 14;
        banner.Format.Font.Color = Stop;
        banner.Format.Alignment = ParagraphAlignment.Center;
        banner.Format.Borders.Width = 1;
        banner.Format.Borders.Color = Stop;
        banner.Format.Shading.Color = new Color(250, 235, 235);
        banner.Format.SpaceAfter = Unit.FromPoint(8);

        if (!string.IsNullOrWhiteSpace(m.VoidReason))
        {
            var motivo = section.AddParagraph(m.VoidReason);
            motivo.Style = "Subtle";
            motivo.Format.Alignment = ParagraphAlignment.Center;
            motivo.Format.SpaceAfter = Unit.FromPoint(8);
        }
    }

    private static void RenderLinesTable(Section section, InvoicePdfModel m, bool compact)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.Rows.LeftIndent = 0;

        var ancho = section.PageSetup.PageWidth
            - section.PageSetup.LeftMargin - section.PageSetup.RightMargin;

        if (compact)
        {
            // En 80 mm no entran cinco columnas legibles: descripción arriba, importe a la derecha.
            table.AddColumn(ancho * 0.68);
            table.AddColumn(ancho * 0.32).Format.Alignment = ParagraphAlignment.Right;
        }
        else
        {
            table.AddColumn(ancho * 0.44);
            table.AddColumn(ancho * 0.10).Format.Alignment = ParagraphAlignment.Right;
            table.AddColumn(ancho * 0.16).Format.Alignment = ParagraphAlignment.Right;
            table.AddColumn(ancho * 0.14).Format.Alignment = ParagraphAlignment.Right;
            table.AddColumn(ancho * 0.16).Format.Alignment = ParagraphAlignment.Right;

            var head = table.AddRow();
            head.Format.Font.Bold = true;
            head.Format.Font.Size = 8;
            head.Borders.Bottom.Width = 0.75;
            head.Borders.Bottom.Color = Rule;
            head.Cells[0].AddParagraph("Concepto");
            head.Cells[1].AddParagraph("Cant.");
            head.Cells[2].AddParagraph("P. unit.");
            head.Cells[3].AddParagraph("Desc.");
            head.Cells[4].AddParagraph("Importe");
        }

        foreach (var l in m.Lines)
        {
            var row = table.AddRow();
            if (compact)
            {
                var desc = row.Cells[0].AddParagraph(l.Description);
                desc.Format.Font.Size = 8;
                row.Cells[0].AddParagraph(
                    $"{Num(l.Quantity, 0)} × {Money(l.UnitPrice)}  {l.TaxMark}").Style = "Subtle";
                row.Cells[1].AddParagraph(Money(l.Total)).Format.Font.Size = 8;
            }
            else
            {
                row.Cells[0].AddParagraph($"{l.Description}  ({l.TaxMark})");
                row.Cells[1].AddParagraph(Num(l.Quantity, 0));
                row.Cells[2].AddParagraph(Money(l.UnitPrice));
                row.Cells[3].AddParagraph(l.DiscountAmount == 0 ? "—" : Money(l.DiscountAmount));
                row.Cells[4].AddParagraph(Money(l.Total));
            }
        }
    }

    private static void RenderTotals(Section section, InvoicePdfModel m, bool compact)
    {
        var t = m.Totals;

        // Los subtotales por tratamiento se imprimen solo si tienen valor: una factura de clínica
        // suele ser toda exenta, y cuatro renglones en cero ocupan lugar sin decir nada.
        AddTotalLine(section, "Importe exento", t.SubtotalExempt, compact, siempre: false);
        AddTotalLine(section, "Importe exonerado", t.SubtotalExonerated, compact, siempre: false);
        AddTotalLine(section, "Importe gravado 15%", t.SubtotalTaxed15, compact, siempre: false);
        AddTotalLine(section, "Importe gravado 18%", t.SubtotalTaxed18, compact, siempre: false);
        AddTotalLine(section, "Descuento", t.DiscountTotal, compact, siempre: false);
        AddTotalLine(section, "ISV", t.TaxTotal, compact, siempre: false);

        var total = section.AddParagraph($"TOTAL A PAGAR   {Money(t.GrandTotal)}");
        total.Format.Font.Bold = true;
        total.Format.Font.Size = compact ? 10 : 12;
        total.Format.Alignment = ParagraphAlignment.Right;
        total.Format.SpaceBefore = Unit.FromPoint(4);
    }

    private static void AddTotalLine(
        Section section, string label, decimal value, bool compact, bool siempre)
    {
        if (!siempre && value == 0m)
        {
            return;
        }
        var p = section.AddParagraph($"{label}   {Money(value)}");
        p.Format.Alignment = ParagraphAlignment.Right;
        p.Format.Font.Size = compact ? 8 : 9;
    }

    /// <summary>
    /// El pie legal: CAI, rango y fecha límite. Van impresos porque el documento tiene que poder
    /// verificarse contra la autorización sin consultar ningún sistema.
    /// </summary>
    private static void RenderFiscalFooter(Section section, InvoicePdfModel m)
    {
        section.AddParagraph($"CAI: {m.Fiscal.Cai}").Style = "Subtle";
        section.AddParagraph($"Rango autorizado: {m.Fiscal.Range}").Style = "Subtle";
        section.AddParagraph(
            $"Fecha límite de emisión: {m.Fiscal.LimitDate:dd/MM/yyyy}").Style = "Subtle";
        section.AddParagraph("Original: cliente · Copia: obligado tributario").Style = "Subtle";
    }

    private static void Divider(Section section)
    {
        var p = section.AddParagraph();
        p.Format.Borders.Bottom.Width = 0.75;
        p.Format.Borders.Bottom.Color = Rule;
        p.Format.SpaceBefore = Unit.FromPoint(4);
        p.Format.SpaceAfter = Unit.FromPoint(4);
    }

    private static void Centered(Section section, string text, string? style = null, bool bold = false)
    {
        var p = section.AddParagraph(text);
        if (style is not null)
        {
            p.Style = style;
        }
        p.Format.Alignment = ParagraphAlignment.Center;
        p.Format.Font.Bold = bold;
    }

    private static string Money(decimal value) => value.ToString("N2", Hn);
    private static string Num(decimal value, int decimals) =>
        value.ToString($"N{decimals}", Hn);
}
