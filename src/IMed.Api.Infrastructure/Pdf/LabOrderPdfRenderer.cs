using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Renderer de PDF de orden de laboratorio (Épica L5). Mismo estilo visual
/// que <see cref="PrescriptionPdfRenderer"/> — header org/practitioner,
/// bloque paciente, bloque del examen (LOINC bold + meta), motivo y notas,
/// línea de firma, footer.
/// </summary>
public sealed class LabOrderPdfRenderer : ILabOrderPdfRenderer
{
    public LabOrderPdfRenderer()
    {
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(LabOrderPdfModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var document = BuildDocument(model);
        var pdfRenderer = new PdfDocumentRenderer(unicode: true)
        {
            Document = document,
        };
        pdfRenderer.RenderDocument();

        using var ms = new MemoryStream();
        pdfRenderer.PdfDocument.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static Document BuildDocument(LabOrderPdfModel model)
    {
        var doc = new Document
        {
            Info =
            {
                Title = "Orden de laboratorio",
                Author = model.Practitioner.DisplayName,
                Subject = $"Orden de laboratorio para {model.Patient.DisplayName}",
            },
        };

        DefineStyles(doc);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.2);

        RenderHeader(section, model);
        RenderTitleBadge(section, model);
        RenderPatientBlock(section, model);
        RenderOrderBlock(section, model);
        RenderSignature(section, model);
        RenderFooter(section, model);

        return doc;
    }

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles["Normal"];
        normal!.Font.Name = "Arial";
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(2);

        var h1 = doc.Styles.AddStyle("OrgTitle", "Normal");
        h1.Font.Size = 14;
        h1.Font.Bold = true;
        h1.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var subt = doc.Styles.AddStyle("Subtle", "Normal");
        subt.Font.Size = 9;
        subt.Font.Color = new Color(110, 110, 110);

        var sect = doc.Styles.AddStyle("SectionTitle", "Normal");
        sect.Font.Size = 10;
        sect.Font.Bold = true;
        sect.Font.Color = new Color(60, 60, 60);
        sect.ParagraphFormat.SpaceBefore = Unit.FromPoint(10);
        sect.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var docTitle = doc.Styles.AddStyle("DocTitle", "Normal");
        docTitle.Font.Size = 13;
        docTitle.Font.Bold = true;
        docTitle.Font.Color = new Color(30, 64, 175);
        docTitle.ParagraphFormat.SpaceBefore = Unit.FromPoint(8);
        docTitle.ParagraphFormat.SpaceAfter = Unit.FromPoint(0);

        var exam = doc.Styles.AddStyle("Exam", "Normal");
        exam.Font.Size = 12;
        exam.Font.Bold = true;
    }

    private static void RenderHeader(Section section, LabOrderPdfModel model)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;
        table.AddColumn(Unit.FromCentimeter(9));
        table.AddColumn(Unit.FromCentimeter(7.6));
        var row = table.AddRow();

        var left = row.Cells[0];
        AddStyledParagraph(left, model.Organization.Name, "OrgTitle");
        if (!string.IsNullOrWhiteSpace(model.Organization.TaxId))
        {
            AddStyledParagraph(left, $"RTN: {model.Organization.TaxId}", "Subtle");
        }

        var right = row.Cells[1];
        right.Format.Alignment = ParagraphAlignment.Right;
        var practName = FormatPractitionerName(model.Practitioner);
        var practTitle = right.AddParagraph();
        practTitle.Format.Alignment = ParagraphAlignment.Right;
        practTitle.AddFormattedText(practName, TextFormat.Bold);
        if (!string.IsNullOrWhiteSpace(model.Practitioner.Specialty))
        {
            AddStyledParagraph(right, model.Practitioner.Specialty!, "Subtle").Format.Alignment = ParagraphAlignment.Right;
        }
        if (!string.IsNullOrWhiteSpace(model.Practitioner.LicenseNumber))
        {
            AddStyledParagraph(right, $"Colegiación: {model.Practitioner.LicenseNumber}", "Subtle").Format.Alignment = ParagraphAlignment.Right;
        }

        var hr = section.AddParagraph();
        hr.Format.SpaceBefore = Unit.FromPoint(4);
        hr.Format.SpaceAfter = Unit.FromPoint(0);
        hr.Format.Borders.Bottom.Width = 0.5;
        hr.Format.Borders.Bottom.Color = new Color(180, 180, 180);
    }

    private static void RenderTitleBadge(Section section, LabOrderPdfModel model)
    {
        AddStyledParagraph(section, "ORDEN DE LABORATORIO", "DocTitle");
        if (!string.IsNullOrWhiteSpace(model.OrderNumber))
        {
            var num = section.AddParagraph();
            num.AddFormattedText($"N.º de orden: {model.OrderNumber}", TextFormat.Bold);
            RenderBarcode(section, model.OrderNumber!);
        }
    }

    /// <summary>
    /// Renderiza el número de orden como código de barras Code128 (F3): ZXing
    /// genera el patrón de módulos y lo dibujamos como una fila de celdas
    /// negras/blancas (sin dependencia de imágenes). Defensivo: cualquier fallo
    /// se ignora — el barcode nunca rompe la generación del PDF.
    /// </summary>
    private static void RenderBarcode(Section section, string content)
    {
        try
        {
            var modules = new ZXing.OneD.Code128Writer().encode(content);
            if (modules is null || modules.Length == 0)
            {
                return;
            }

            const int quiet = 10;              // zona muda (módulos) a cada lado
            const double moduleWidthPt = 0.9;  // ancho de 1 módulo en puntos (~0.32 mm)

            // Agrupar módulos consecutivos del mismo color en "runs" (menos columnas).
            var runs = new List<(int Width, bool Black)> { (quiet, false) };
            var i = 0;
            while (i < modules.Length)
            {
                var val = modules[i];
                var len = 1;
                while (i + len < modules.Length && modules[i + len] == val)
                {
                    len++;
                }
                runs.Add((len, val));
                i += len;
            }
            runs.Add((quiet, false));

            var table = section.AddTable();
            table.Borders.Width = 0;
            table.Format.SpaceBefore = Unit.FromPoint(4);
            foreach (var r in runs)
            {
                table.AddColumn(Unit.FromPoint(r.Width * moduleWidthPt));
            }
            var row = table.AddRow();
            row.Height = Unit.FromCentimeter(1.0);
            row.HeightRule = MigraDocCore.DocumentObjectModel.Tables.RowHeightRule.Exactly;
            for (var c = 0; c < runs.Count; c++)
            {
                if (runs[c].Black)
                {
                    row.Cells[c].Shading.Color = Colors.Black;
                }
            }
        }
        catch
        {
            // El barcode es decorativo respecto al número impreso; nunca rompe el PDF.
        }
    }

    private static void RenderPatientBlock(Section section, LabOrderPdfModel model)
    {
        AddStyledParagraph(section, "PACIENTE", "SectionTitle");

        var p = model.Patient;
        var p1 = section.AddParagraph();
        p1.AddFormattedText(p.DisplayName, TextFormat.Bold);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.IdentifierValue))
        {
            var label = string.IsNullOrWhiteSpace(p.IdentifierLabel) ? "ID" : p.IdentifierLabel!;
            meta.Add($"{label}: {p.IdentifierValue}");
        }
        if (!string.IsNullOrWhiteSpace(p.BirthDate))
        {
            meta.Add($"FN: {p.BirthDate}");
        }
        if (!string.IsNullOrWhiteSpace(p.Gender))
        {
            meta.Add($"Sexo: {FormatGender(p.Gender!)}");
        }
        if (meta.Count > 0)
        {
            AddStyledParagraph(section, string.Join("   ·   ", meta), "Subtle");
        }
    }

    private static void RenderOrderBlock(Section section, LabOrderPdfModel model)
    {
        AddStyledParagraph(
            section,
            model.Items.Count > 1 ? "EXÁMENES SOLICITADOS" : "EXAMEN SOLICITADO",
            "SectionTitle");

        if (model.Items.Count == 0)
        {
            AddStyledParagraph(section, "Sin exámenes.", "Subtle");
            return;
        }

        var index = 1;
        foreach (var item in model.Items)
        {
            var examLine = section.AddParagraph();
            examLine.Style = "Exam";
            examLine.AddText(model.Items.Count > 1 ? $"{index}.  {item.ExamLabel}" : item.ExamLabel);
            if (!string.IsNullOrWhiteSpace(item.LoincCode))
            {
                AddStyledParagraph(section, $"LOINC {item.LoincCode}", "Subtle");
            }

            var sub = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.PriorityLabel))
            {
                sub.Add($"Prioridad: {item.PriorityLabel}");
            }
            if (!string.IsNullOrWhiteSpace(item.OccurrenceLabel))
            {
                sub.Add($"Fecha sugerida: {item.OccurrenceLabel}");
            }
            if (sub.Count > 0)
            {
                AddStyledParagraph(section, string.Join("   ·   ", sub), "Subtle");
            }

            if (!string.IsNullOrWhiteSpace(item.AvailabilityLabel))
            {
                var av = section.AddParagraph();
                av.AddFormattedText("Disponibilidad: ", TextFormat.Italic);
                av.AddText(item.AvailabilityLabel!);
            }

            if (!string.IsNullOrWhiteSpace(item.Reason))
            {
                var r = section.AddParagraph();
                r.AddFormattedText("Motivo: ", TextFormat.Italic);
                r.AddText(item.Reason!);
            }
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                var n = section.AddParagraph();
                n.AddFormattedText("Indicaciones al laboratorio: ", TextFormat.Italic);
                n.AddText(item.Notes!);
            }

            index++;
        }
    }

    private static void RenderSignature(Section section, LabOrderPdfModel model)
    {
        var spacer = section.AddParagraph();
        spacer.Format.SpaceBefore = Unit.FromCentimeter(2.2);

        var sigLine = section.AddParagraph();
        sigLine.Format.Alignment = ParagraphAlignment.Right;
        sigLine.AddText("______________________________________");

        var sigName = section.AddParagraph();
        sigName.Format.Alignment = ParagraphAlignment.Right;
        sigName.AddFormattedText(FormatPractitionerName(model.Practitioner), TextFormat.Bold);

        if (!string.IsNullOrWhiteSpace(model.Practitioner.LicenseNumber))
        {
            var sigLicense = section.AddParagraph();
            sigLicense.Format.Alignment = ParagraphAlignment.Right;
            sigLicense.Format.Font.Size = 9;
            sigLicense.AddText($"Colegiación: {model.Practitioner.LicenseNumber}");
        }
    }

    private static void RenderFooter(Section section, LabOrderPdfModel model)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = new Color(120, 120, 120);
        var issued = model.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        footer.AddText($"Emitida {issued}   ·   {model.Organization.Name}");
    }

    private static Paragraph AddStyledParagraph(Section section, string text, string styleName)
    {
        var p = section.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static Paragraph AddStyledParagraph(MigraDocCore.DocumentObjectModel.Tables.Cell cell, string text, string styleName)
    {
        var p = cell.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static string FormatPractitionerName(PdfPractitionerInfo p)
    {
        if (!string.IsNullOrWhiteSpace(p.Prefix))
        {
            return $"{p.Prefix} {p.DisplayName}".Trim();
        }
        return p.DisplayName;
    }

    private static string FormatGender(string code) => code switch
    {
        "male" => "Masculino",
        "female" => "Femenino",
        "other" => "Otro",
        "unknown" => "Sin especificar",
        _ => code,
    };
}
