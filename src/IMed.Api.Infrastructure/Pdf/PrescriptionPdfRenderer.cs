using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Implementación del renderer de receta médica (Épica K5) con PdfSharpCore +
/// MigraDocCore. Layout simple A4: header (organización + tax id) y bloque
/// derecho con datos del médico; bloque de paciente; lista de medicamentos
/// como tabla; línea de firma manuscrita al final.
///
/// <para>El renderer es stateless (singleton). Inicializa el font resolver de
/// PdfSharpCore una sola vez via constructor estático — el resolver bundle
/// expone una familia Arial cross-platform suficiente para el piloto.</para>
/// </summary>
public sealed class PrescriptionPdfRenderer : IPrescriptionPdfRenderer
{
    public PrescriptionPdfRenderer()
    {
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(PrescriptionPdfModel model)
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

    private static Document BuildDocument(PrescriptionPdfModel model)
    {
        var doc = new Document
        {
            Info =
            {
                Title = "Receta médica",
                Author = model.Practitioner.DisplayName,
                Subject = $"Receta para {model.Patient.DisplayName}",
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
        RenderPatientBlock(section, model);
        RenderMedicationsList(section, model);
        RenderGeneralIndications(section, model);
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

        var rxTitle = doc.Styles.AddStyle("RxTitle", "Normal");
        rxTitle.Font.Size = 11;
        rxTitle.Font.Bold = true;

        var rxBody = doc.Styles.AddStyle("RxBody", "Normal");
        rxBody.Font.Size = 10;
    }

    private static void RenderHeader(Section section, PrescriptionPdfModel model)
    {
        var table = section.AddTable();
        table.Borders.Width = 0;

        // Con logo el encabezado pasa a tres columnas —logo, clínica, profesional— sin cambiar
        // el ancho total (3.4 + 5.6 + 7.6 = 9 + 7.6).
        var logo = model.Organization.Logo;
        var withLogo = logo is not null;
        if (withLogo)
        {
            table.AddColumn(Unit.FromCentimeter(3.4));
        }
        table.AddColumn(Unit.FromCentimeter(withLogo ? 5.6 : 9));
        table.AddColumn(Unit.FromCentimeter(7.6));

        var row = table.AddRow();
        row.VerticalAlignment = VerticalAlignment.Center;

        var column = 0;
        if (withLogo)
        {
            PdfLogoRenderer.TryDraw(row.Cells[column++], logo);
        }

        var left = row.Cells[column];
        AddStyledParagraph(left, model.Organization.Name, "OrgTitle");
        if (!string.IsNullOrWhiteSpace(model.Organization.TaxId))
        {
            AddStyledParagraph(left, $"RTN: {model.Organization.TaxId}", "Subtle");
        }

        var right = row.Cells[column + 1];
        right.Format.Alignment = ParagraphAlignment.Right;
        var practName = FormatPractitionerName(model.Practitioner);
        AddStyledParagraph(right, practName, "RxTitle").Format.Alignment = ParagraphAlignment.Right;
        if (!string.IsNullOrWhiteSpace(model.Practitioner.Specialty))
        {
            AddStyledParagraph(right, model.Practitioner.Specialty!, "Subtle").Format.Alignment = ParagraphAlignment.Right;
        }
        if (!string.IsNullOrWhiteSpace(model.Practitioner.LicenseNumber))
        {
            AddStyledParagraph(right, $"Colegiación: {model.Practitioner.LicenseNumber}", "Subtle").Format.Alignment = ParagraphAlignment.Right;
        }

        // Separador
        var hr = section.AddParagraph();
        hr.Format.SpaceBefore = Unit.FromPoint(4);
        hr.Format.SpaceAfter = Unit.FromPoint(0);
        hr.Format.Borders.Bottom.Width = 0.5;
        hr.Format.Borders.Bottom.Color = new Color(180, 180, 180);
    }

    private static void RenderPatientBlock(Section section, PrescriptionPdfModel model)
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

    private static void RenderMedicationsList(Section section, PrescriptionPdfModel model)
    {
        AddStyledParagraph(section, "Rp.", "SectionTitle");

        if (model.Items.Count == 0)
        {
            AddStyledParagraph(section, "Sin medicamentos.", "Subtle");
            return;
        }

        var index = 1;
        foreach (var item in model.Items)
        {
            var line = section.AddParagraph();
            line.Format.SpaceBefore = Unit.FromPoint(6);
            line.Format.LeftIndent = Unit.FromCentimeter(0.6);
            line.AddFormattedText($"{index}.  {item.Medication}", TextFormat.Bold);

            if (!string.IsNullOrWhiteSpace(item.Dosage))
            {
                AddStyledParagraph(section, item.Dosage!, "RxBody").Format.LeftIndent = Unit.FromCentimeter(1.1);
            }

            var sub = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Route))
            {
                sub.Add(item.Route!);
            }
            if (!string.IsNullOrWhiteSpace(item.Quantity))
            {
                sub.Add($"Cantidad: {item.Quantity}");
            }
            if (sub.Count > 0)
            {
                AddStyledParagraph(section, string.Join("   ·   ", sub), "Subtle").Format.LeftIndent = Unit.FromCentimeter(1.1);
            }

            if (!string.IsNullOrWhiteSpace(item.Indications))
            {
                var ind = section.AddParagraph();
                ind.Format.LeftIndent = Unit.FromCentimeter(1.1);
                ind.AddFormattedText("Indicaciones: ", TextFormat.Italic);
                ind.AddText(item.Indications!);
            }

            index++;
        }
    }

    private static void RenderGeneralIndications(Section section, PrescriptionPdfModel model)
    {
        if (string.IsNullOrWhiteSpace(model.GeneralIndications))
        {
            return;
        }

        AddStyledParagraph(section, "INDICACIONES GENERALES", "SectionTitle");
        AddStyledParagraph(section, model.GeneralIndications!, "RxBody");
    }

    private static Paragraph AddStyledParagraph(Section section, string text, string styleName)
    {
        var p = section.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static Paragraph AddStyledParagraph(Cell cell, string text, string styleName)
    {
        var p = cell.AddParagraph(text);
        p.Style = styleName;
        return p;
    }

    private static void RenderSignature(Section section, PrescriptionPdfModel model)
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

    private static void RenderFooter(Section section, PrescriptionPdfModel model)
    {
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = new Color(120, 120, 120);
        var issued = model.IssuedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        footer.AddText($"Emitida {issued}   ·   {model.Organization.Name}");
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
