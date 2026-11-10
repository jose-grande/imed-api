using System.Globalization;
using IMed.Api.Application.Abstractions;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Renderer del PDF de etiqueta de muestra (R6-F4b).
///
/// <para><b>Una muestra = una etiqueta = una página del tamaño físico exacto del formato.</b>
/// Así la impresora la deposita sin escalar, sea una térmica de rollo o una láser con hoja de
/// etiquetas. Cada etiqueta es su propia página, de modo que un mismo PDF puede mezclar
/// tamaños cuando el plan tiene contenedores con rollos distintos (un tubo EDTA y un frasco de
/// orina en la misma orden).</para>
///
/// <para><b>Dibujo absoluto, no flujo de documento.</b> La primera versión apilaba párrafos con
/// MigraDoc: cuando el contenido no entraba (un nombre largo en mayúsculas, una lista de pruebas
/// extensa), MigraDoc abría otra página y el barcode y el texto salían repartidos en varias
/// etiquetas — observación de todos los técnicos. Ahora cada elemento se dibuja en coordenadas
/// fijas dentro del área útil, con el texto medido de verdad: lo que no entra se acorta con "…"
/// o se omite, pero nunca empuja contenido a otra etiqueta.</para>
///
/// <para><b>Layout adaptativo.</b> No hay una plantilla fija: el contenido se elige según el
/// área útil, en orden de prioridad (barcode y accession siempre; luego paciente, fecha de
/// nacimiento, recipiente; y en las grandes, pruebas y orden). La alternativa —dejar que el
/// laboratorio marque qué campos imprimir— permite configurar una combinación que no entra.</para>
/// </summary>
public sealed class SpecimenLabelPdfRenderer : ISpecimenLabelPdfRenderer
{
    /// <summary>Zona muda del Code128, en módulos, a cada lado. Por debajo de 10 el lector falla.</summary>
    private const int QuietZoneModules = 10;

    /// <summary>Alto del barcode como fracción del alto útil de la etiqueta.</summary>
    private const double BarcodeHeightRatio = 0.42;

    private const double MinBarcodeHeightMm = 4.0;
    private const double MaxBarcodeHeightMm = 14.0;

    /// <summary>Cuerpo mínimo al que se reduce el accession antes de acortarlo: es el dato
    /// autoritativo de la etiqueta y se prefiere más chico a incompleto.</summary>
    private const double MinAccessionFontPt = 5.0;

    /// <summary>Separación vertical entre bloques, en puntos.</summary>
    private const double GapPt = 1.0;

    private const string FontFamily = "Arial";
    private const string Ellipsis = "…";

    private const double PointsPerMm = 72.0 / 25.4;

    private static readonly XPdfFontOptions FontOptions = new(PdfFontEncoding.Unicode);
    private static readonly XBrush TextBrush = XBrushes.Black;
    private static readonly XBrush MutedBrush = new XSolidBrush(XColor.FromArgb(90, 90, 90));

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

    /// <summary>Un renglón de texto a ubicar debajo del barcode, en orden de prioridad.</summary>
    private sealed record TextLine(string Text, double SizePt, bool Bold, int MaxLines = 1, bool Muted = false);

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

        using var document = new PdfDocument();
        document.Info.Title = sheet.Labels.Count == 1
            ? $"Etiqueta {sheet.Labels[0].Accession}"
            : $"Etiquetas de muestra ({sheet.Labels.Count})";
        document.Info.Subject = "Etiqueta de muestra de laboratorio";

        foreach (var label in sheet.Labels)
        {
            RenderLabel(document, label);
        }

        using var ms = new MemoryStream();
        document.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static void RenderLabel(PdfDocument document, SpecimenLabelPage label)
    {
        var format = label.Format;
        var page = document.AddPage();
        page.Width = XUnit.FromMillimeter((double)format.WidthMm);
        page.Height = XUnit.FromMillimeter((double)format.HeightMm);

        // Los offsets de calibración corren el contenido dentro del papel: el área útil
        // conserva su tamaño, solo se mueve.
        var margin = (double)format.MarginMm;
        var usableWidthMm = Math.Max(1, (double)format.WidthMm - (margin * 2));
        var usableHeightMm = Math.Max(1, (double)format.HeightMm - (margin * 2));
        var area = new XRect(
            Mm(Math.Max(0, margin + (double)format.OffsetXMm)),
            Mm(Math.Max(0, margin + (double)format.OffsetYMm)),
            Mm(usableWidthMm),
            Mm(usableHeightMm));

        var profile = ResolveProfile(usableWidthMm, usableHeightMm);

        using var gfx = XGraphics.FromPdfPage(page);
        // Red de seguridad: nada se dibuja fuera del área útil, ni siquiera por redondeo.
        gfx.IntersectClip(area);

        // 1. Accession: se mide primero porque su renglón está garantizado, y el barcode
        //    toma el alto restante hasta su proporción objetivo.
        var (accessionText, accessionFont) = FitAccession(gfx, label, profile, area.Width);
        var accessionHeight = accessionFont.GetHeight();

        var desiredBarcodePt = Mm(Math.Clamp(
            usableHeightMm * BarcodeHeightRatio, MinBarcodeHeightMm, MaxBarcodeHeightMm));
        var barcodeHeight = Math.Max(0, Math.Min(desiredBarcodePt, area.Height - accessionHeight - GapPt));

        var y = area.Top;
        if (barcodeHeight > 0 && DrawBarcode(gfx, label.BarcodeValue, area.Left, y, area.Width, barcodeHeight))
        {
            y += barcodeHeight + GapPt;
        }

        gfx.DrawString(accessionText, accessionFont, TextBrush,
            new XRect(area.Left, y, area.Width, accessionHeight), XStringFormats.TopLeft);
        y += accessionHeight;

        if (profile == LabelProfile.Compact)
        {
            return;
        }

        // 2. El resto, en orden de prioridad, mientras quede alto. Lo que no entra se omite.
        foreach (var line in BuildLines(label, profile))
        {
            var font = new XFont(FontFamily, line.SizePt, line.Bold ? XFontStyle.Bold : XFontStyle.Regular, FontOptions);
            var lineHeight = font.GetHeight();
            var room = (int)Math.Floor((area.Bottom - y) / lineHeight);
            if (room <= 0)
            {
                break;
            }

            var rows = Wrap(gfx, line.Text, font, area.Width, Math.Min(line.MaxLines, room));
            foreach (var row in rows)
            {
                gfx.DrawString(row, font, line.Muted ? MutedBrush : TextBrush,
                    new XRect(area.Left, y, area.Width, lineHeight), XStringFormats.TopLeft);
                y += lineHeight;
            }
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

    private static IEnumerable<TextLine> BuildLines(SpecimenLabelPage label, LabelProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(label.PatientName))
        {
            yield return new TextLine(label.PatientName!.Trim(), 7, Bold: true);
        }

        if (!string.IsNullOrWhiteSpace(label.PatientBirthDate))
        {
            yield return new TextLine($"FN {label.PatientBirthDate}", 6, Bold: false);
        }

        var container = new List<string>();
        if (label.Sequence > 0)
        {
            container.Add($"#{label.Sequence}");
        }
        if (!string.IsNullOrWhiteSpace(label.ContainerLabel))
        {
            container.Add(label.ContainerLabel!.Trim());
        }
        if (profile == LabelProfile.Extended && label.RequiredVolumeMl is { } vol)
        {
            container.Add($"{vol.ToString("0.#", CultureInfo.InvariantCulture)} mL");
        }
        if (container.Count > 0)
        {
            yield return new TextLine(string.Join(" · ", container), 6, Bold: false);
        }

        if (profile != LabelProfile.Extended)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(label.Tests))
        {
            // Las pruebas sí pueden ocupar dos renglones: es la única lista de la etiqueta.
            yield return new TextLine(label.Tests!.Trim(), 5.5, Bold: false, MaxLines: 2);
        }

        var footer = new List<string>();
        if (!string.IsNullOrWhiteSpace(label.OrderNumber))
        {
            footer.Add(label.OrderNumber!.Trim());
        }
        footer.Add(label.PrintedAt.ToLocalTime().ToString("dd/MM/yy HH:mm", CultureInfo.InvariantCulture));
        yield return new TextLine(string.Join(" · ", footer), 5.5, Bold: false, Muted: true);
    }

    /// <summary>
    /// El accession se achica hasta <see cref="MinAccessionFontPt"/> antes de acortarse: es
    /// lo que el técnico teclea si el lector no toma el barcode. En las compactas lleva pegado
    /// el número de secuencia, solo si entra completo.
    /// </summary>
    private static (string Text, XFont Font) FitAccession(
        XGraphics gfx, SpecimenLabelPage label, LabelProfile profile, double width)
    {
        var accession = label.Accession.Trim();
        var withSequence = profile == LabelProfile.Compact && label.Sequence > 0
            ? $"{accession}  #{label.Sequence}"
            : accession;

        for (var size = profile == LabelProfile.Compact ? 7.0 : 8.0; ; size -= 0.5)
        {
            var font = new XFont(FontFamily, size, XFontStyle.Bold, FontOptions);
            if (gfx.MeasureString(withSequence, font).Width <= width)
            {
                return (withSequence, font);
            }
            if (gfx.MeasureString(accession, font).Width <= width)
            {
                return (accession, font);
            }
            if (size <= MinAccessionFontPt)
            {
                return (Truncate(gfx, accession, font, width), font);
            }
        }
    }

    /// <summary>
    /// Dibuja el Code128 como rectángulos negros escalados al ancho útil: ZXing da el patrón de
    /// módulos y el símbolo completo (con zonas mudas) ocupa todo el ancho, el máximo tamaño
    /// legible que permite la etiqueta.
    ///
    /// <para>Defensivo: si el barcode falla, la etiqueta igual sale con el accession legible —
    /// una etiqueta sin barras se puede teclear, una excepción deja el tubo sin rotular.</para>
    /// </summary>
    private static bool DrawBarcode(XGraphics gfx, string content, double x, double y, double width, double height)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        bool[] modules;
        try
        {
            modules = new ZXing.OneD.Code128Writer().encode(content);
        }
        catch
        {
            return false;
        }
        if (modules is null || modules.Length == 0)
        {
            return false;
        }

        var moduleWidth = width / (modules.Length + (QuietZoneModules * 2));
        var i = 0;
        while (i < modules.Length)
        {
            var len = 1;
            while (i + len < modules.Length && modules[i + len] == modules[i])
            {
                len++;
            }
            if (modules[i])
            {
                gfx.DrawRectangle(TextBrush,
                    x + ((QuietZoneModules + i) * moduleWidth), y, len * moduleWidth, height);
            }
            i += len;
        }
        return true;
    }

    /// <summary>
    /// Reparte el texto en hasta <paramref name="maxLines"/> renglones por palabras, con el
    /// ancho medido; el último renglón se acorta con "…" si sobra texto.
    /// </summary>
    private static List<string> Wrap(XGraphics gfx, string text, XFont font, double width, int maxLines)
    {
        var rows = new List<string>();
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var w = 0;
        while (w < words.Length && rows.Count < maxLines)
        {
            if (rows.Count == maxLines - 1)
            {
                rows.Add(Truncate(gfx, string.Join(' ', words[w..]), font, width));
                break;
            }

            var current = words[w];
            w++;
            while (w < words.Length && gfx.MeasureString($"{current} {words[w]}", font).Width <= width)
            {
                current = $"{current} {words[w]}";
                w++;
            }
            // Una sola palabra más ancha que la etiqueta también se acorta.
            rows.Add(Truncate(gfx, current, font, width));
        }
        return rows;
    }

    /// <summary>Acorta el texto con "…" hasta que su ancho medido entre en <paramref name="width"/>.</summary>
    private static string Truncate(XGraphics gfx, string text, XFont font, double width)
    {
        if (gfx.MeasureString(text, font).Width <= width)
        {
            return text;
        }

        // Búsqueda binaria del prefijo más largo que entra con la elipsis.
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (gfx.MeasureString(string.Concat(text.AsSpan(0, mid).TrimEnd(), Ellipsis), font).Width <= width)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo == 0 ? string.Empty : string.Concat(text.AsSpan(0, lo).TrimEnd(), Ellipsis);
    }

    private static double Mm(double mm) => mm * PointsPerMm;
}
