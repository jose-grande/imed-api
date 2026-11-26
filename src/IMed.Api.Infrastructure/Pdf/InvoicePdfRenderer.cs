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

    /// <summary>Carta: impresora láser o de tinta, los grises salen como grises.</summary>
    private static readonly Palette LetterPalette = new(
        Ink: new Color(30, 30, 30),
        Soft: new Color(110, 110, 110),
        Rule: new Color(210, 210, 210),
        Stop: new Color(160, 40, 40),
        StopFill: new Color(250, 235, 235));

    /// <summary>
    /// Ticket: la impresora térmica es de un bit — cada punto quema o no quema. Un gris se
    /// rasteriza como trama de puntos sueltos: el texto secundario sale granulado y casi ilegible,
    /// y las líneas claras directamente desaparecen. Todo va en negro puro; la jerarquía la dan el
    /// tamaño y la negrita, no el color.
    /// </summary>
    private static readonly Palette ThermalPalette = new(
        Ink: Colors.Black,
        Soft: Colors.Black,
        Rule: Colors.Black,
        Stop: Colors.Black,
        StopFill: null);

    // La hora se imprime en la zona de la clínica (IClinicClock), no en la del servidor:
    // ToLocalTime() usa la del servidor, que en staging y producción es UTC (seis horas de más).
    private readonly IClinicClock _clock;

    public InvoicePdfRenderer(IClinicClock clock)
    {
        _clock = clock;
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(InvoicePdfModel model, InvoicePrintFormat format = InvoicePrintFormat.Letter)
    {
        ArgumentNullException.ThrowIfNull(model);
        model = model with
        {
            Fiscal = model.Fiscal with { IssuedAt = _clock.ToClinicTime(model.Fiscal.IssuedAt) },
            Modifies = model.Modifies is { } o && o.IssuedAt != DateTimeOffset.MinValue
                ? o with { IssuedAt = _clock.ToClinicTime(o.IssuedAt) }
                : model.Modifies,
        };

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
        var doc = NewDocument(m, LetterPalette);
        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.Letter;
        // El ancho se fija a mano: con solo PageFormat, PageWidth vale 0 hasta que MigraDoc lo
        // resuelve al renderizar, y la tabla del detalle —que reparte ese ancho— salía con las
        // columnas de cantidad, precio e importe en ancho cero: invisibles en el papel.
        section.PageSetup.PageWidth = Unit.FromInch(8.5);
        section.PageSetup.PageHeight = Unit.FromInch(11);
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.0);

        RenderVoidBanner(section, m, LetterPalette);

        // Emisor
        PdfLogoRenderer.TryDraw(section, m.Issuer.Logo);
        section.AddParagraph(m.Issuer.LegalName).Style = "OrgTitle";
        foreach (var linea in IssuerLines(m.Issuer))
        {
            section.AddParagraph(linea).Style = "Subtle";
        }

        Divider(section, LetterPalette);

        // Documento
        var titulo = section.AddParagraph(m.Fiscal.DocumentTypeName.ToUpperInvariant());
        titulo.Style = "DocTitle";
        var numero = section.AddParagraph(m.Fiscal.FullNumber);
        numero.Style = "Number";

        section.AddParagraph(
            $"Fecha de emisión: {m.Fiscal.IssuedAt:dd/MM/yyyy HH:mm}").Style = "Subtle";
        foreach (var linea in ModifiesLines(m))
        {
            section.AddParagraph(linea).Style = "Subtle";
        }

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
        section.AddParagraph(BuyerIdLine(m.Buyer)).Style = "Subtle";
        foreach (var linea in ExemptionLines(m.Buyer))
        {
            section.AddParagraph(linea).Style = "Subtle";
        }

        // Detalle
        section.AddParagraph("Detalle").Style = "SectionTitle";
        RenderLinesTable(section, m, LetterPalette, compact: false);

        // Totales
        RenderTotals(section, m, compact: false);

        if (!string.IsNullOrWhiteSpace(m.Totals.AmountInWords))
        {
            var letras = section.AddParagraph(m.Totals.AmountInWords);
            letras.Style = "Subtle";
            letras.Format.SpaceBefore = Unit.FromPoint(6);
        }

        RenderReceiverBlock(section, m, compact: false);

        // Pie legal
        Divider(section, LetterPalette);
        RenderFiscalFooter(section, m);

        return doc;
    }

    // ── Ticket 80 mm ────────────────────────────────────────────────────────

    private static Document BuildThermal(InvoicePdfModel m)
    {
        var p = ThermalPalette;
        var doc = NewDocument(m, p);
        var section = doc.AddSection();

        // 80 mm de ancho, alto libre: la impresora térmica corta donde termina el contenido.
        section.PageSetup.PageWidth = Unit.FromMillimeter(80);
        section.PageSetup.PageHeight = Unit.FromMillimeter(280);
        section.PageSetup.TopMargin = Unit.FromMillimeter(4);
        section.PageSetup.BottomMargin = Unit.FromMillimeter(6);
        section.PageSetup.LeftMargin = Unit.FromMillimeter(4);
        section.PageSetup.RightMargin = Unit.FromMillimeter(4);

        doc.Styles["Normal"]!.Font.Size = 8;

        RenderVoidBanner(section, m, p);

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

        // El ticket de 80 mm es una Factura en papel térmico, no el Ticket de máquina registradora
        // (art. 17): lleva todos los datos del emisor del art. 10.1, como la carta.
        foreach (var linea in IssuerLines(m.Issuer))
        {
            Centered(section, linea, "Subtle");
        }

        Divider(section, p);

        Centered(section, m.Fiscal.DocumentTypeName.ToUpperInvariant(), bold: true);
        Centered(section, m.Fiscal.FullNumber, bold: true);
        Centered(section, $"Fecha de emisión: {m.Fiscal.IssuedAt.ToString("dd/MM/yyyy HH:mm", Hn)}", "Subtle");
        foreach (var linea in ModifiesLines(m))
        {
            Centered(section, linea, "Subtle");
        }

        if (m.IsReprint)
        {
            Centered(section, "REIMPRESIÓN", bold: true);
        }

        Divider(section, p);

        section.AddParagraph(m.Buyer.Name);
        section.AddParagraph(BuyerIdLine(m.Buyer)).Style = "Subtle";
        foreach (var linea in ExemptionLines(m.Buyer))
        {
            section.AddParagraph(linea).Style = "Subtle";
        }

        Divider(section, p);
        RenderLinesTable(section, m, p, compact: true);
        Divider(section, p);
        RenderTotals(section, m, compact: true);

        if (!string.IsNullOrWhiteSpace(m.Totals.AmountInWords))
        {
            var letras = section.AddParagraph(m.Totals.AmountInWords);
            letras.Style = "Subtle";
            letras.Format.SpaceBefore = Unit.FromPoint(4);
        }

        RenderReceiverBlock(section, m, compact: true);

        Divider(section, p);
        RenderFiscalFooter(section, m);

        return doc;
    }

    // ── Piezas compartidas ──────────────────────────────────────────────────

    private static Document NewDocument(InvoicePdfModel m, Palette palette)
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
        DefineStyles(doc, palette);
        return doc;
    }

    private static void DefineStyles(Document doc, Palette palette)
    {
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;
        normal.Font.Color = palette.Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var org = doc.Styles.AddStyle("OrgTitle", "Normal");
        org.Font.Size = 14;
        org.Font.Bold = true;
        org.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var subtle = doc.Styles.AddStyle("Subtle", "Normal");
        subtle.Font.Size = 8;
        subtle.Font.Color = palette.Soft;

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
    private static void RenderVoidBanner(Section section, InvoicePdfModel m, Palette palette)
    {
        if (!m.Fiscal.IsVoided)
        {
            return;
        }

        var banner = section.AddParagraph("DOCUMENTO ANULADO");
        banner.Format.Font.Bold = true;
        banner.Format.Font.Size = 14;
        banner.Format.Font.Color = palette.Stop;
        banner.Format.Alignment = ParagraphAlignment.Center;
        banner.Format.Borders.Width = 1;
        banner.Format.Borders.Color = palette.Stop;
        if (palette.StopFill is { } fill)
        {
            banner.Format.Shading.Color = fill;
        }
        banner.Format.SpaceAfter = Unit.FromPoint(8);

        if (!string.IsNullOrWhiteSpace(m.VoidReason))
        {
            var motivo = section.AddParagraph(m.VoidReason);
            motivo.Style = "Subtle";
            motivo.Format.Alignment = ParagraphAlignment.Center;
            motivo.Format.SpaceAfter = Unit.FromPoint(8);
        }
    }

    private static void RenderLinesTable(
        Section section, InvoicePdfModel m, Palette palette, bool compact)
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
            head.Borders.Bottom.Color = palette.Rule;
            head.Cells[0].AddParagraph("Concepto");
            head.Cells[1].AddParagraph("Cant.");
            head.Cells[2].AddParagraph("P. unit. (L)");
            head.Cells[3].AddParagraph("Desc. (L)");
            head.Cells[4].AddParagraph("Importe (L)");
        }

        foreach (var l in m.Lines)
        {
            var row = table.AddRow();
            if (compact)
            {
                var desc = row.Cells[0].AddParagraph(l.Description);
                desc.Format.Font.Size = 8;
                row.Cells[0].AddParagraph(
                    $"{Num(l.Quantity, 0)} × L {Money(l.UnitPrice)}  {l.TaxMark}").Style = "Subtle";
                if (l.DiscountAmount != 0m)
                {
                    row.Cells[0].AddParagraph($"Descuento L {Money(l.DiscountAmount)}").Style = "Subtle";
                }
                row.Cells[1].AddParagraph($"L {Money(l.Total)}").Format.Font.Size = 8;
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
        // Requisito del formato desde el Acuerdo 725-2018: va aunque sea cero.
        AddTotalLine(section, "Descuentos y rebajas otorgados", t.DiscountTotal, compact, siempre: true);
        // ISV discriminado por tarifa (art. 11.1.i). Si las líneas no lo separan (documentos sin
        // líneas gravadas reconocibles), el total va como ISV sin tarifa: nunca se pierde.
        AddTotalLine(section, "ISV 15%", t.Tax15, compact, siempre: false);
        AddTotalLine(section, "ISV 18%", t.Tax18, compact, siempre: false);
        var sinTarifa = t.TaxTotal - t.Tax15 - t.Tax18;
        AddTotalLine(section, "ISV", sinTarifa, compact, siempre: false);

        // Una nota de crédito no se paga: es lo que se acredita (art. 26.5, importe total).
        var etiqueta = m.Modifies is null ? "TOTAL A PAGAR" : "TOTAL DE LA NOTA";
        var total = section.AddParagraph($"{etiqueta}   L {Money(t.GrandTotal)}");
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
        var p = section.AddParagraph($"{label}   L {Money(value)}");
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
        section.AddParagraph("Original: Cliente · Copia: Obligado tributario emisor").Style = "Subtle";
    }

    /// <summary>Datos del emisor del art. 10.1, en el orden en que se leen (sin la razón social, que va de título).</summary>
    private static IEnumerable<string> IssuerLines(PdfIssuerInfo i)
    {
        if (!string.IsNullOrWhiteSpace(i.TradeName)
            && !string.Equals(i.TradeName.Trim(), i.LegalName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            yield return i.TradeName.Trim();
        }
        if (!string.IsNullOrWhiteSpace(i.TaxId))
        {
            yield return $"RTN {i.TaxId}";
        }
        if (!string.IsNullOrWhiteSpace(i.HeadOfficeAddress))
        {
            yield return $"Casa matriz: {i.HeadOfficeAddress}";
            if (!string.IsNullOrWhiteSpace(i.Address))
            {
                yield return $"Establecimiento: {i.Address}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(i.Address))
        {
            yield return i.Address;
        }
        var contacto = string.Join(" · ", new[]
        {
            string.IsNullOrWhiteSpace(i.Phone) ? null : $"Tel. {i.Phone}",
            string.IsNullOrWhiteSpace(i.Email) ? null : i.Email,
        }.Where(x => x is not null));
        if (contacto.Length > 0)
        {
            yield return contacto;
        }
    }

    /// <summary>
    /// Art. 11: RTN para crédito fiscal; si no, "CONSUMIDOR FINAL". Sobre L 10,000 el número de
    /// identidad va en el espacio del RTN, y se imprime tal cual se capturó.
    /// </summary>
    private static string BuyerIdLine(PdfBuyerInfo b) =>
        string.IsNullOrWhiteSpace(b.TaxId) ? "RTN: CONSUMIDOR FINAL" : $"RTN / identidad: {b.TaxId}";

    /// <summary>
    /// Datos del adquirente exonerado (art. 10.8). Es un requisito del formato: el espacio va
    /// siempre, lleno o en blanco.
    /// </summary>
    private static IEnumerable<string> ExemptionLines(PdfBuyerInfo b)
    {
        var e = b.Exemption;
        yield return $"N.º Orden de Compra Exenta: {e?.PurchaseOrderNumber ?? "—"}";
        yield return $"N.º Constancia de Registro de Exonerados: {e?.ExemptRegistryNumber ?? "—"}";
        yield return $"N.º Registro SAG: {e?.SagRegistryNumber ?? "—"}";
    }

    /// <summary>Nota de crédito: el comprobante que modifica (art. 26.3) y el motivo (art. 26.4).</summary>
    private static IEnumerable<string> ModifiesLines(InvoicePdfModel m)
    {
        if (m.Modifies is { } o)
        {
            yield return $"Modifica la factura {o.FullNumber}";
            yield return $"CAI de la factura: {o.Cai}";
            if (o.IssuedAt != DateTimeOffset.MinValue)
            {
                yield return $"Fecha de emisión de la factura: {o.IssuedAt:dd/MM/yyyy}";
            }
        }
        if (!string.IsNullOrWhiteSpace(m.Reference))
        {
            yield return m.Reference;
        }
    }

    /// <summary>Nota de crédito: quien la recibe deja nombre, identidad y firma (art. 26.7).</summary>
    private static void RenderReceiverBlock(Section section, InvoicePdfModel m, bool compact)
    {
        if (m.Modifies is null)
        {
            return;
        }
        var titulo = section.AddParagraph("Recibe la nota de crédito");
        titulo.Style = "SectionTitle";
        var raya = compact ? "______________________" : "________________________________________";
        section.AddParagraph($"Nombres y apellidos: {raya}");
        section.AddParagraph($"Identidad: {raya}");
        section.AddParagraph($"Firma: {raya}");
    }

    private static void Divider(Section section, Palette palette)
    {
        var p = section.AddParagraph();
        p.Format.Borders.Bottom.Width = 0.75;
        p.Format.Borders.Bottom.Color = palette.Rule;
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

    private sealed record Palette(Color Ink, Color Soft, Color Rule, Color Stop, Color? StopFill);

    private static string Money(decimal value) => value.ToString("N2", Hn);
    private static string Num(decimal value, int decimals) =>
        value.ToString($"N{decimals}", Hn);
}
