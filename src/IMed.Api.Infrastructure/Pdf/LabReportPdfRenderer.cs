using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Renderer del informe de resultados de una orden. Mismo lenguaje visual que los renderers de
/// orden y receta, con las reglas que vienen del oficio y del cliente:
///
/// <list type="number">
///   <item><b>Área → panel → subárea.</b> Lo que no tiene resultados no llega al modelo, así que
///   no hay títulos vacíos.</item>
///   <item><b>Fuera de rango: solo * o **.</b> En el mismo color y peso que el resto de la línea,
///   sin columna de "Alto"/"Crítico alto": el paciente lee este papel (observación del cliente).
///   Los asteriscos además sobreviven a la fotocopia en blanco y negro.</item>
///   <item><b>El panel es un encabezado, no una fila.</b> La observación "grouper" de FHIR no
///   tiene valor propio.</item>
///   <item><b>Solo lo validado.</b> Si quedan pruebas sin validar, se avisa: el documento no se
///   puede tomar por completo.</item>
/// </list>
/// </summary>
public sealed class LabReportPdfRenderer : ILabReportPdfRenderer
{
    private static readonly Color Ink = new(30, 30, 30);
    private static readonly Color Muted = new(110, 110, 110);
    private static readonly Color Rule = new(210, 210, 210);
    private static readonly Color HeaderBg = new(243, 244, 246);

    public LabReportPdfRenderer()
    {
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(LabReportPdfModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var document = BuildDocument(model);
        var pdfRenderer = new PdfDocumentRenderer(unicode: true) { Document = document };
        pdfRenderer.RenderDocument();

        using var ms = new MemoryStream();
        pdfRenderer.PdfDocument.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static Document BuildDocument(LabReportPdfModel model)
    {
        var doc = new Document
        {
            Info =
            {
                Title = "Informe de laboratorio",
                Author = model.Organization.Name,
                Subject = $"Resultados de laboratorio de {model.Patient.DisplayName}",
            },
        };

        DefineStyles(doc);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.6);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);

        RenderPageFooter(section, model);
        RenderHeader(section, model);
        RenderPatientBlock(section, model);
        RenderResults(section, model);
        RenderPending(section, model);
        RenderSignature(section, model);

        return doc;
    }

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles["Normal"]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 9.5;
        normal.Font.Color = Ink;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var org = doc.Styles.AddStyle("OrgTitle", "Normal");
        org.Font.Size = 14;
        org.Font.Bold = true;
        org.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var docTitle = doc.Styles.AddStyle("DocTitle", "Normal");
        docTitle.Font.Size = 12;
        docTitle.Font.Bold = true;
        docTitle.Font.Color = new Color(30, 64, 175);

        var subtle = doc.Styles.AddStyle("Subtle", "Normal");
        subtle.Font.Size = 8.5;
        subtle.Font.Color = Muted;

        var sect = doc.Styles.AddStyle("SectionTitle", "Normal");
        sect.Font.Size = 9.5;
        sect.Font.Bold = true;
        sect.Font.Color = new Color(60, 60, 60);
        sect.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        sect.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        var group = doc.Styles.AddStyle("GroupTitle", "Normal");
        group.Font.Size = 10;
        group.Font.Bold = true;
        group.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        group.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        group.ParagraphFormat.KeepWithNext = true;

        var area = doc.Styles.AddStyle("AreaTitle", "Normal");
        area.Font.Size = 11;
        area.Font.Bold = true;
        area.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
        area.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);
        area.ParagraphFormat.Shading.Color = HeaderBg;
        area.ParagraphFormat.Alignment = ParagraphAlignment.Center;
        area.ParagraphFormat.KeepWithNext = true;

        var subsection = doc.Styles.AddStyle("SubsectionTitle", "Normal");
        subsection.Font.Size = 9.5;
        subsection.Font.Bold = true;
        subsection.Font.Underline = Underline.Single;
        subsection.ParagraphFormat.SpaceBefore = Unit.FromPoint(6);
        subsection.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);
        subsection.ParagraphFormat.KeepWithNext = true;

        var footer = doc.Styles["Footer"]!;
        footer.Font.Size = 8;
        footer.Font.Color = Muted;
    }

    private static void RenderHeader(Section section, LabReportPdfModel model)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;

        // Con logo, el encabezado pasa a tres columnas: logo, datos del laboratorio, datos de
        // la orden. El ancho total se mantiene (3.4 + 7.0 + 7.0 = 10.4 + 7.0), así que la
        // columna derecha sigue alineada con la tabla de resultados de abajo.
        var logo = model.Organization.Logo;
        var withLogo = logo is not null;
        if (withLogo)
        {
            table.AddColumn(Unit.FromCentimeter(3.4));
        }
        table.AddColumn(Unit.FromCentimeter(withLogo ? 7.0 : 10.4));
        table.AddColumn(Unit.FromCentimeter(7.0));

        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;

        // Si los bytes no se pueden decodificar la celda queda vacía y el informe sale igual:
        // no vale la pena perder el documento por una imagen.
        var column = 0;
        if (withLogo)
        {
            PdfLogoRenderer.TryDraw(row.Cells[column++], logo);
        }

        var left = row.Cells[column];
        AddStyled(left, model.Organization.Name, "OrgTitle");
        if (!string.IsNullOrWhiteSpace(model.Organization.TaxId))
        {
            AddStyled(left, $"RTN {model.Organization.TaxId}", "Subtle");
        }
        AddStyled(left, "Informe de laboratorio", "DocTitle");

        var right = row.Cells[column + 1];
        right.Format.Alignment = ParagraphAlignment.Right;
        if (!string.IsNullOrWhiteSpace(model.Requisition))
        {
            AddStyled(right, $"Orden {model.Requisition}", "Normal");
        }

        var rule = section.AddParagraph();
        rule.Format.SpaceBefore = Unit.FromPoint(6);
        rule.Format.Borders.Bottom.Width = 0.75;
        rule.Format.Borders.Bottom.Color = Rule;
    }

    private static void RenderPatientBlock(Section section, LabReportPdfModel model)
    {
        AddStyled(section, "PACIENTE", "SectionTitle");

        var table = section.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(8.7));
        table.AddColumn(Unit.FromCentimeter(8.7));
        var row = table.AddRow();

        var name = row.Cells[0].AddParagraph(model.Patient.DisplayName);
        name.Format.Font.Bold = true;
        name.Format.Font.Size = 11;

        var demo = new List<string>();
        if (!string.IsNullOrWhiteSpace(model.Patient.BirthDate))
        {
            demo.Add($"F. nac. {model.Patient.BirthDate}");
        }
        if (!string.IsNullOrWhiteSpace(model.Patient.Gender))
        {
            demo.Add(FormatGender(model.Patient.Gender!));
        }
        if (demo.Count > 0)
        {
            AddStyled(row.Cells[0], string.Join(" · ", demo), "Subtle");
        }
        if (!string.IsNullOrWhiteSpace(model.Patient.IdentifierValue))
        {
            AddStyled(row.Cells[0],
                $"{model.Patient.IdentifierLabel ?? "Identificación"}: {model.Patient.IdentifierValue}", "Subtle");
        }

        var right = row.Cells[1];
        if (model.RequestedBy is { } req)
        {
            AddStyled(right, $"Solicitado por: {FormatPractitioner(req)}", "Normal");
        }
        // Las tres fechas cuentan la trazabilidad del estudio y son lo primero que pregunta
        // un médico cuando un valor no le cierra.
        AddDate(right, "Recepción", model.ReceivedAt);
        AddDate(right, "Liberación", model.ReleasedAt);
        if (!string.IsNullOrWhiteSpace(model.ExternalLabName))
        {
            AddStyled(right, $"Procesado en: {model.ExternalLabName}", "Subtle");
        }
    }

    private static void RenderResults(Section section, LabReportPdfModel model)
    {
        foreach (var area in model.Areas)
        {
            AddStyled(section, area.Title.ToUpper(CultureInfo.CurrentCulture), "AreaTitle");

            foreach (var block in area.Blocks)
            {
                if (!string.IsNullOrWhiteSpace(block.Title))
                {
                    AddStyled(section, block.Title!.ToUpper(CultureInfo.CurrentCulture), "GroupTitle");
                    if (!string.IsNullOrWhiteSpace(block.ValidatedBy))
                    {
                        var by = AddStyled(section, $"Resultados validados por: {block.ValidatedBy}", "Subtle");
                        by.Format.KeepWithNext = true;
                    }
                }

                foreach (var sub in block.Subsections)
                {
                    if (!string.IsNullOrWhiteSpace(sub.Title))
                    {
                        AddStyled(section, sub.Title!, "SubsectionTitle");
                    }
                    RenderLines(section, sub.Lines);
                }

                if (!string.IsNullOrWhiteSpace(block.Conclusion))
                {
                    var c = section.AddParagraph();
                    c.Format.SpaceBefore = Unit.FromPoint(4);
                    c.AddFormattedText("Comentario: ", TextFormat.Bold);
                    c.AddText(block.Conclusion!);
                }
            }
        }

        // Neutra a propósito: dice qué significan los asteriscos sin calificar el valor.
        var legend = section.AddParagraph(
            "* / ** Valor fuera del intervalo de referencia.   (C) Resultado corregido.");
        legend.Style = "Subtle";
        legend.Format.SpaceBefore = Unit.FromPoint(8);
    }

    private static void RenderLines(Section section, IReadOnlyList<PdfReportLine> lines)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.Rows.LeftIndent = 0;
        table.AddColumn(Unit.FromCentimeter(7.4));   // Descripción
        table.AddColumn(Unit.FromCentimeter(3.8));   // Resultado
        table.AddColumn(Unit.FromCentimeter(2.6));   // Unidad
        table.AddColumn(Unit.FromCentimeter(3.6));   // Referencia

        var head = table.AddRow();
        head.HeadingFormat = true;
        head.Format.Font.Bold = true;
        head.Format.Font.Size = 8;
        head.Format.Font.Color = Muted;
        head.Cells[0].AddParagraph("Descripción");
        head.Cells[1].AddParagraph("Resultado");
        head.Cells[2].AddParagraph("Unidad");
        head.Cells[3].AddParagraph("Referencia");

        foreach (var line in lines)
        {
            var row = table.AddRow();
            row.TopPadding = Unit.FromPoint(1.5);
            row.BottomPadding = Unit.FromPoint(1.5);
            row.Borders.Bottom.Width = 0.5;
            row.Borders.Bottom.Color = Rule;

            var nameCell = row.Cells[0].AddParagraph(line.Name);
            if (line.IsCorrected)
            {
                nameCell.AddText("  (C)");
            }

            // Mismo color y peso que el resto de la línea: la marca es solo el asterisco.
            row.Cells[1].AddParagraph(string.IsNullOrEmpty(line.Mark) ? line.Value : $"{line.Value} {line.Mark}");
            row.Cells[2].AddParagraph(line.Unit ?? "");
            row.Cells[3].AddParagraph(line.ReferenceRange ?? "");
        }
    }

    /// <summary>Aviso de estudios de la orden aún sin validar: no se imprimen, pero se dice.</summary>
    private static void RenderPending(Section section, LabReportPdfModel model)
    {
        if (model.PendingCount <= 0)
        {
            return;
        }
        var p = section.AddParagraph(model.PendingCount == 1
            ? "Hay 1 estudio de esta orden pendiente de validación. Se entregará cuando esté listo."
            : $"Hay {model.PendingCount} estudios de esta orden pendientes de validación. Se entregarán cuando estén listos.");
        p.Format.SpaceBefore = Unit.FromPoint(8);
        p.Format.Font.Bold = true;
        p.Format.Borders.Width = 0.75;
        p.Format.Borders.Color = Rule;
        p.Format.Borders.Distance = Unit.FromPoint(4);
    }

    private static void RenderSignature(Section section, LabReportPdfModel model)
    {
        var validators = model.Validators ?? [];
        if (validators.Count == 0)
        {
            // Liberado sin profesional resuelto: se dice, en vez de dejar un espacio en blanco
            // que parezca una firma faltante.
            var p = section.AddParagraph("Resultados liberados en el sistema. Profesional validador no registrado.");
            p.Style = "Subtle";
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.SpaceBefore = Unit.FromCentimeter(1.0);
            return;
        }

        foreach (var v in validators)
        {
            var line = section.AddParagraph();
            line.Format.Alignment = ParagraphAlignment.Center;
            line.Format.Borders.Top.Width = 0.75;
            line.Format.Borders.Top.Color = Rule;
            line.Format.SpaceBefore = Unit.FromCentimeter(1.6);
            line.Format.LeftIndent = Unit.FromCentimeter(5.0);
            line.Format.RightIndent = Unit.FromCentimeter(5.0);
            line.Format.KeepWithNext = true;
            line.AddFormattedText(FormatPractitioner(v), TextFormat.Bold);

            if (!string.IsNullOrWhiteSpace(v.LicenseNumber))
            {
                var lic = section.AddParagraph($"Colegiación {v.LicenseNumber}");
                lic.Style = "Subtle";
                lic.Format.Alignment = ParagraphAlignment.Center;
                lic.Format.KeepWithNext = true;
            }
            var role = section.AddParagraph("Validó y liberó resultados");
            role.Style = "Subtle";
            role.Format.Alignment = ParagraphAlignment.Center;
        }
    }

    private static void RenderPageFooter(Section section, LabReportPdfModel model)
    {
        var printed = model.PrintedAt == default ? DateTimeOffset.Now : model.PrintedAt;
        var p = section.Footers.Primary.AddParagraph();
        p.Style = "Footer";
        p.Format.Alignment = ParagraphAlignment.Center;
        p.AddText($"{model.Organization.Name}  ·  Impreso {printed.ToLocalTime():dd/MM/yyyy HH:mm}  ·  Página ");
        p.AddPageField();
        p.AddText(" de ");
        p.AddNumPagesField();
    }

    // --- helpers ------------------------------------------------------------

    private static void AddDate(Cell cell, string label, DateTimeOffset? value)
    {
        if (value is null)
        {
            return;
        }
        AddStyled(cell, $"{label}: {value.Value.ToLocalTime():dd/MM/yyyy HH:mm}", "Subtle");
    }

    private static Paragraph AddStyled(Section section, string text, string styleName)
    {
        var p = section.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static Paragraph AddStyled(Cell cell, string text, string styleName)
    {
        var p = cell.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static string FormatPractitioner(PdfPractitionerInfo p) =>
        string.IsNullOrWhiteSpace(p.Prefix) ? p.DisplayName : $"{p.Prefix} {p.DisplayName}";

    private static string FormatGender(string code) => code.ToLower(CultureInfo.InvariantCulture) switch
    {
        "male" => "Masculino",
        "female" => "Femenino",
        "other" => "Otro",
        _ => code,
    };
}
