using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Renderer del informe de resultados. Mismo lenguaje visual que los renderers de orden y
/// receta, con tres diferencias que vienen del oficio y no del gusto:
///
/// <list type="number">
///   <item><b>Lo anormal se marca sin depender del color.</b> Estos informes se imprimen en
///   blanco y negro y se fotocopian: un valor alto señalado solo con rojo desaparece. Va en
///   negrita, con un asterisco y con la palabra escrita.</item>
///   <item><b>El panel es un encabezado, no una fila.</b> La observación "grouper" de FHIR no
///   tiene valor propio; imprimirla como una determinación más dejaba una línea vacía en el
///   papel.</item>
///   <item><b>Un informe no liberado grita que es preliminar.</b> Es el documento que sale de la
///   institución: uno preliminar sin marcar puede archivarse en una historia clínica como si
///   fuera definitivo.</item>
/// </list>
/// </summary>
public sealed class LabReportPdfRenderer : ILabReportPdfRenderer
{
    private static readonly Color Ink = new(30, 30, 30);
    private static readonly Color Muted = new(110, 110, 110);
    private static readonly Color Rule = new(210, 210, 210);
    private static readonly Color Alert = new(180, 30, 30);
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
        if (!model.IsFinal)
        {
            RenderPreliminaryBanner(section);
        }
        RenderPatientBlock(section, model);
        RenderResults(section, model);
        RenderConclusion(section, model);
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

        var footer = doc.Styles["Footer"]!;
        footer.Font.Size = 8;
        footer.Font.Color = Muted;
    }

    private static void RenderHeader(Section section, LabReportPdfModel model)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(10.4));
        table.AddColumn(Unit.FromCentimeter(7.0));
        var row = table.AddRow();

        AddStyled(row.Cells[0], model.Organization.Name, "OrgTitle");
        if (!string.IsNullOrWhiteSpace(model.Organization.TaxId))
        {
            AddStyled(row.Cells[0], $"RTN {model.Organization.TaxId}", "Subtle");
        }
        AddStyled(row.Cells[0], "Informe de laboratorio", "DocTitle");

        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        if (!string.IsNullOrWhiteSpace(model.OrderNumber))
        {
            AddStyled(right, $"Orden {model.OrderNumber}", "Normal");
        }
        if (!string.IsNullOrWhiteSpace(model.AccessionNumber))
        {
            AddStyled(right, $"Accession {model.AccessionNumber}", "Subtle");
        }
        if (!string.IsNullOrWhiteSpace(model.Requisition))
        {
            AddStyled(right, $"Requisición {model.Requisition}", "Subtle");
        }

        var rule = section.AddParagraph();
        rule.Format.SpaceBefore = Unit.FromPoint(6);
        rule.Format.Borders.Bottom.Width = 0.75;
        rule.Format.Borders.Bottom.Color = Rule;
    }

    /// <summary>
    /// Aviso para el informe no liberado. Deliberadamente grande y con borde: la alternativa
    /// realista es que alguien archive un preliminar creyendo que es definitivo.
    /// </summary>
    private static void RenderPreliminaryBanner(Section section)
    {
        var table = section.AddTable();
        table.Borders.Width = 1.0;
        table.Borders.Color = Alert;
        table.AddColumn(Unit.FromCentimeter(17.4));
        var cell = table.AddRow().Cells[0];
        cell.Format.Alignment = ParagraphAlignment.Center;
        cell.Format.SpaceBefore = Unit.FromPoint(4);
        cell.Format.SpaceAfter = Unit.FromPoint(4);

        var p = cell.AddParagraph("RESULTADO PRELIMINAR — NO VALIDADO");
        p.Format.Font.Bold = true;
        p.Format.Font.Size = 11;
        p.Format.Font.Color = Alert;

        var q = cell.AddParagraph("Pendiente de validación profesional. No usar para decisiones clínicas.");
        q.Format.Font.Size = 8.5;
        q.Format.Font.Color = Alert;
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
        AddDate(right, "Toma", model.CollectedAt);
        AddDate(right, "Recepción", model.ReceivedAt);
        AddDate(right, "Liberación", model.ReleasedAt);
        if (!string.IsNullOrWhiteSpace(model.ExternalLabName))
        {
            AddStyled(right, $"Procesado en: {model.ExternalLabName}", "Subtle");
        }
    }

    private static void RenderResults(Section section, LabReportPdfModel model)
    {
        AddStyled(section, "RESULTADOS", "SectionTitle");

        if (model.Groups.Count == 0)
        {
            AddStyled(section, "El informe no tiene determinaciones cargadas.", "Subtle");
            return;
        }

        foreach (var group in model.Groups)
        {
            // Un panel encabeza su bloque; las pruebas sueltas no necesitan encabezado propio.
            if (group.Lines.Count > 1 || model.Groups.Count > 1)
            {
                var title = section.AddParagraph(group.Title);
                title.Style = "GroupTitle";
                if (!string.IsNullOrWhiteSpace(group.LoincCode))
                {
                    var code = section.AddParagraph(group.LoincCode!);
                    code.Style = "Subtle";
                    code.Format.SpaceAfter = Unit.FromPoint(3);
                }
            }

            RenderGroupTable(section, group);
        }

        var legend = section.AddParagraph(
            "* Valor fuera del rango de referencia.   ** Valor crítico.   (C) Resultado corregido.");
        legend.Style = "Subtle";
        legend.Format.SpaceBefore = Unit.FromPoint(6);
    }

    private static void RenderGroupTable(Section section, PdfReportGroup group)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.Rows.LeftIndent = 0;
        table.AddColumn(Unit.FromCentimeter(6.6));   // Determinación
        table.AddColumn(Unit.FromCentimeter(3.2));   // Resultado
        table.AddColumn(Unit.FromCentimeter(2.2));   // Unidad
        table.AddColumn(Unit.FromCentimeter(3.4));   // Referencia
        table.AddColumn(Unit.FromCentimeter(2.0));   // Marca

        var head = table.AddRow();
        head.Shading.Color = HeaderBg;
        head.Format.Font.Bold = true;
        head.Format.Font.Size = 8.5;
        head.TopPadding = Unit.FromPoint(3);
        head.BottomPadding = Unit.FromPoint(3);
        head.Cells[0].AddParagraph("Determinación");
        head.Cells[1].AddParagraph("Resultado");
        head.Cells[2].AddParagraph("Unidad");
        head.Cells[3].AddParagraph("Referencia");
        head.Cells[4].AddParagraph("");

        foreach (var line in group.Lines)
        {
            var row = table.AddRow();
            row.TopPadding = Unit.FromPoint(2.5);
            row.BottomPadding = Unit.FromPoint(2.5);
            row.Borders.Bottom.Width = 0.5;
            row.Borders.Bottom.Color = Rule;

            var nameCell = row.Cells[0].AddParagraph(line.Name);
            if (line.IsCorrected)
            {
                nameCell.AddFormattedText("  (C)", TextFormat.NotBold).Font.Color = Muted;
            }
            if (!string.IsNullOrWhiteSpace(line.LoincCode))
            {
                var code = row.Cells[0].AddParagraph(line.LoincCode!);
                code.Style = "Subtle";
            }

            // El valor anormal se distingue por peso y símbolo, no por color: estos informes se
            // fotocopian en blanco y negro.
            var value = row.Cells[1].AddParagraph(line.Value + (line.IsCritical ? " **" : line.IsAbnormal ? " *" : ""));
            value.Format.Font.Bold = line.IsAbnormal || line.IsCritical;
            if (line.IsCritical)
            {
                value.Format.Font.Color = Alert;
            }

            row.Cells[2].AddParagraph(line.Unit ?? "");
            row.Cells[3].AddParagraph(line.ReferenceRange ?? "—");

            if (!string.IsNullOrWhiteSpace(line.FlagLabel))
            {
                var flag = row.Cells[4].AddParagraph(line.FlagLabel!);
                flag.Format.Font.Size = 8.5;
                flag.Format.Font.Bold = line.IsCritical;
                flag.Format.Font.Color = line.IsCritical ? Alert : Ink;
            }
        }
    }

    private static void RenderConclusion(Section section, LabReportPdfModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Conclusion))
        {
            return;
        }
        AddStyled(section, "OBSERVACIONES", "SectionTitle");
        section.AddParagraph(model.Conclusion!);
    }

    private static void RenderSignature(Section section, LabReportPdfModel model)
    {
        var block = section.AddParagraph();
        block.Format.SpaceBefore = Unit.FromCentimeter(1.4);

        if (model.ValidatedBy is { } v)
        {
            var line = section.AddParagraph();
            line.Format.Alignment = ParagraphAlignment.Center;
            line.Format.Borders.Top.Width = 0.75;
            line.Format.Borders.Top.Color = Rule;
            line.Format.SpaceBefore = Unit.FromCentimeter(1.0);
            line.Format.LeftIndent = Unit.FromCentimeter(5.0);
            line.Format.RightIndent = Unit.FromCentimeter(5.0);
            line.AddFormattedText(FormatPractitioner(v), TextFormat.Bold);

            if (!string.IsNullOrWhiteSpace(v.LicenseNumber))
            {
                var lic = section.AddParagraph($"Colegiación {v.LicenseNumber}");
                lic.Style = "Subtle";
                lic.Format.Alignment = ParagraphAlignment.Center;
            }
            var role = section.AddParagraph("Validó y liberó el informe");
            role.Style = "Subtle";
            role.Format.Alignment = ParagraphAlignment.Center;
        }
        else if (model.IsFinal)
        {
            // Liberado sin profesional resuelto: se dice, en vez de dejar un espacio en blanco
            // que parezca una firma faltante.
            var p = section.AddParagraph("Informe liberado en el sistema. Profesional validador no registrado.");
            p.Style = "Subtle";
            p.Format.Alignment = ParagraphAlignment.Center;
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
