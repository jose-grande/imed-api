using System.Globalization;
using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.Tables;
using MigraDocCore.Rendering;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Renderer del PDF de etiqueta de muestra (R6-F4b).
///
/// <para><b>Una etiqueta = una página del tamaño físico exacto del formato.</b> Así la
/// impresora la deposita sin escalar, sea una térmica de rollo o una láser con hoja de
/// etiquetas. Cada etiqueta lleva su propia <c>Section</c>, de modo que un mismo PDF puede
/// mezclar tamaños cuando el plan tiene contenedores con rollos distintos (un tubo EDTA y
/// un frasco de orina en la misma orden).</para>
///
/// <para><b>Layout adaptativo.</b> No hay una plantilla fija: el contenido se elige según el
/// área útil. En una cryovial de 33 × 13 mm apenas entra el código de barras y el accession;
/// en un frasco de 76 × 51 mm entra además el paciente, el tubo, las pruebas y la orden. La
/// alternativa —dejar que el laboratorio marque qué campos imprimir— permite configurar una
/// combinación que no entra y corta el texto justo donde importa (el nombre del paciente).</para>
/// </summary>
public sealed class SpecimenLabelPdfRenderer : ISpecimenLabelPdfRenderer
{
    /// <summary>Zona muda del Code128, en módulos, a cada lado. Por debajo de 10 el lector falla.</summary>
    private const int QuietZoneModules = 10;

    /// <summary>Alto del barcode como fracción del alto útil de la etiqueta.</summary>
    private const double BarcodeHeightRatio = 0.42;

    private const double MinBarcodeHeightMm = 4.0;
    private const double MaxBarcodeHeightMm = 14.0;

    /// <summary>Ancho medio de carácter en Arial como fracción del cuerpo. Sirve para
    /// estimar cuántos caracteres entran por línea y truncar antes de que MigraDoc corte.</summary>
    private const double ArialAverageCharRatio = 0.52;

    private const double PointsPerMm = 72.0 / 25.4;

    /// <summary>Cuánto contenido entra, derivado del área útil de la etiqueta.</summary>
    private enum LabelProfile
    {
        /// <summary>Solo barcode + accession. Cryoviales y etiquetas de tapa.</summary>
        Compact,

        /// <summary>+ paciente, fecha de nacimiento y recipiente. El caso típico de tubo.</summary>
        Standard,

        /// <summary>+ pruebas, volumen, orden y fecha/hora. Frascos y etiquetas grandes.</summary>
        Extended,
    }

    public SpecimenLabelPdfRenderer()
    {
        PdfFontEnvironment.Ensure();
    }

    public byte[] Render(SpecimenLabelSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (sheet.Labels.Count == 0)
        {
            throw new ArgumentException("Se requiere al menos una etiqueta.", nameof(sheet));
        }

        var document = new Document
        {
            Info =
            {
                Title = sheet.Labels.Count == 1
                    ? $"Etiqueta {sheet.Labels[0].Accession}"
                    : $"Etiquetas de muestra ({sheet.Labels.Count})",
                Subject = "Etiqueta de muestra de laboratorio",
            },
        };

        DefineStyles(document);

        foreach (var label in sheet.Labels)
        {
            RenderLabel(document, label);
        }

        var renderer = new PdfDocumentRenderer(unicode: true) { Document = document };
        renderer.RenderDocument();

        using var ms = new MemoryStream();
        renderer.PdfDocument.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static void DefineStyles(Document doc)
    {
        var normal = doc.Styles["Normal"];
        normal!.Font.Name = "Arial";
        normal.Font.Size = 6;
        normal.ParagraphFormat.SpaceBefore = 0;
        normal.ParagraphFormat.SpaceAfter = 0;
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Single;
    }

    private static void RenderLabel(Document doc, SpecimenLabelPage label)
    {
        var format = label.Format;
        var section = doc.AddSection();

        section.PageSetup.PageWidth = Unit.FromMillimeter((double)format.WidthMm);
        section.PageSetup.PageHeight = Unit.FromMillimeter((double)format.HeightMm);
        section.PageSetup.Orientation = Orientation.Portrait;

        // Los offsets de calibración corren el contenido dentro del papel: lo que se suma de
        // un lado se resta del opuesto, así el área útil no cambia de tamaño (solo se mueve).
        var margin = (double)format.MarginMm;
        var offsetX = (double)format.OffsetXMm;
        var offsetY = (double)format.OffsetYMm;
        section.PageSetup.LeftMargin = Unit.FromMillimeter(Math.Max(0, margin + offsetX));
        section.PageSetup.RightMargin = Unit.FromMillimeter(Math.Max(0, margin - offsetX));
        section.PageSetup.TopMargin = Unit.FromMillimeter(Math.Max(0, margin + offsetY));
        section.PageSetup.BottomMargin = Unit.FromMillimeter(Math.Max(0, margin - offsetY));

        var usableWidthMm = (double)format.WidthMm - (margin * 2);
        var usableHeightMm = (double)format.HeightMm - (margin * 2);
        var profile = ResolveProfile(usableWidthMm, usableHeightMm);

        RenderBarcode(section, label.BarcodeValue, usableWidthMm, usableHeightMm);
        RenderAccession(section, label, profile, usableWidthMm);

        if (profile == LabelProfile.Compact)
        {
            return;
        }

        RenderPatient(section, label, usableWidthMm);
        RenderContainer(section, label, profile, usableWidthMm);

        if (profile == LabelProfile.Extended)
        {
            RenderExtended(section, label, usableWidthMm);
        }
    }

    /// <summary>
    /// Elige el perfil por área útil. Los cortes salen de lo que físicamente entra: debajo de
    /// ~16 mm de alto solo hay lugar para barcode + accession, y debajo de ~28 mm no queda
    /// espacio para la lista de pruebas sin comerse el barcode.
    /// </summary>
    private static LabelProfile ResolveProfile(double usableWidthMm, double usableHeightMm)
    {
        if (usableHeightMm < 16 || usableWidthMm < 34)
        {
            return LabelProfile.Compact;
        }
        if (usableHeightMm < 28)
        {
            return LabelProfile.Standard;
        }
        return LabelProfile.Extended;
    }

    /// <summary>
    /// Dibuja el Code128 como una fila de celdas negras/blancas, escalado para ocupar todo el
    /// ancho útil (mismo enfoque que <see cref="LabOrderPdfRenderer"/>: ZXing da el patrón de
    /// módulos y MigraDoc lo pinta, sin dependencia de imágenes).
    ///
    /// <para>Defensivo: si el barcode falla, la etiqueta igual sale con el accession legible —
    /// una etiqueta sin barras se puede teclear, una excepción deja el tubo sin rotular.</para>
    /// </summary>
    private static void RenderBarcode(Section section, string content, double usableWidthMm, double usableHeightMm)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        try
        {
            var modules = new ZXing.OneD.Code128Writer().encode(content);
            if (modules is null || modules.Length == 0)
            {
                return;
            }

            // Agrupar módulos consecutivos del mismo color en "runs" (menos columnas en la tabla).
            var runs = new List<(int Width, bool Black)> { (QuietZoneModules, false) };
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
            runs.Add((QuietZoneModules, false));

            // El módulo se escala para que el símbolo completo (con zonas mudas) ocupe
            // exactamente el ancho útil: es el máximo tamaño legible que permite la etiqueta.
            var totalModules = modules.Length + (QuietZoneModules * 2);
            var moduleWidthPt = usableWidthMm * PointsPerMm / totalModules;

            var barcodeHeightMm = Math.Clamp(
                usableHeightMm * BarcodeHeightRatio, MinBarcodeHeightMm, MaxBarcodeHeightMm);

            var table = section.AddTable();
            table.Borders.Width = 0;
            foreach (var r in runs)
            {
                table.AddColumn(Unit.FromPoint(r.Width * moduleWidthPt));
            }

            var row = table.AddRow();
            row.Height = Unit.FromMillimeter(barcodeHeightMm);
            row.HeightRule = RowHeightRule.Exactly;
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
            // El barcode es un atajo de lectura; el accession impreso es el dato autoritativo.
        }
    }

    private static void RenderAccession(
        Section section, SpecimenLabelPage label, LabelProfile profile, double usableWidthMm)
    {
        var size = profile == LabelProfile.Compact ? 7 : 8;
        var p = section.AddParagraph();
        p.Format.SpaceBefore = Unit.FromPoint(1);
        p.Format.Font.Size = size;
        p.Format.Font.Bold = true;
        p.AddText(Fit(label.Accession, usableWidthMm, size, bold: true));

        // El número de secuencia (order of draw) va pegado al accession en las chicas,
        // donde no hay lugar para una línea propia.
        if (profile == LabelProfile.Compact && label.Sequence > 0)
        {
            p.AddText($"  #{label.Sequence}");
        }
    }

    private static void RenderPatient(Section section, SpecimenLabelPage label, double usableWidthMm)
    {
        if (!string.IsNullOrWhiteSpace(label.PatientName))
        {
            var p = section.AddParagraph();
            p.Format.Font.Size = 7;
            p.Format.Font.Bold = true;
            p.AddText(Fit(label.PatientName!, usableWidthMm, 7, bold: true));
        }

        if (!string.IsNullOrWhiteSpace(label.PatientBirthDate))
        {
            var p = section.AddParagraph();
            p.Format.Font.Size = 6;
            p.AddText(Fit($"FN {label.PatientBirthDate}", usableWidthMm, 6));
        }
    }

    private static void RenderContainer(
        Section section, SpecimenLabelPage label, LabelProfile profile, double usableWidthMm)
    {
        var parts = new List<string>();
        if (label.Sequence > 0)
        {
            parts.Add($"#{label.Sequence}");
        }
        if (!string.IsNullOrWhiteSpace(label.ContainerLabel))
        {
            parts.Add(label.ContainerLabel!);
        }
        if (profile == LabelProfile.Extended && label.RequiredVolumeMl is { } vol)
        {
            parts.Add($"{vol.ToString("0.#", CultureInfo.InvariantCulture)} mL");
        }
        if (parts.Count == 0)
        {
            return;
        }

        var p = section.AddParagraph();
        p.Format.Font.Size = 6;
        p.AddText(Fit(string.Join(" · ", parts), usableWidthMm, 6));
    }

    private static void RenderExtended(Section section, SpecimenLabelPage label, double usableWidthMm)
    {
        if (!string.IsNullOrWhiteSpace(label.Tests))
        {
            var p = section.AddParagraph();
            p.Format.SpaceBefore = Unit.FromPoint(1);
            p.Format.Font.Size = 5.5;
            // Las pruebas sí pueden ocupar dos líneas: es la única lista de la etiqueta.
            p.AddText(Fit(label.Tests!, usableWidthMm * 2, 5.5));
        }

        var footer = new List<string>();
        if (!string.IsNullOrWhiteSpace(label.OrderNumber))
        {
            footer.Add(label.OrderNumber!);
        }
        footer.Add(label.PrintedAt.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));

        var f = section.AddParagraph();
        f.Format.SpaceBefore = Unit.FromPoint(1);
        f.Format.Font.Size = 5.5;
        f.Format.Font.Color = new Color(90, 90, 90);
        f.AddText(Fit(string.Join(" · ", footer), usableWidthMm, 5.5));
    }

    /// <summary>
    /// Trunca el texto a lo que entra en el ancho dado. MigraDoc no expone medición de texto en
    /// esta capa, así que se estima con el ancho medio de carácter de Arial — conservador a
    /// propósito: es preferible un nombre acortado con "…" a uno partido a mitad de renglón.
    /// </summary>
    private static string Fit(string text, double availableWidthMm, double fontSizePt, bool bold = false)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var charRatio = bold ? ArialAverageCharRatio * 1.06 : ArialAverageCharRatio;
        var charWidthMm = fontSizePt * charRatio / PointsPerMm;
        if (charWidthMm <= 0)
        {
            return text;
        }

        var maxChars = (int)Math.Floor(availableWidthMm / charWidthMm);
        if (maxChars <= 0)
        {
            return string.Empty;
        }
        if (text.Length <= maxChars)
        {
            return text;
        }
        return maxChars <= 1 ? text[..1] : string.Concat(text.AsSpan(0, maxChars - 1), "…");
    }
}
