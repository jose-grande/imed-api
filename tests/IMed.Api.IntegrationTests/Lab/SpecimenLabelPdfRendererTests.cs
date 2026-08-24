using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Pdf;
using PdfSharpCore.Pdf.IO;
using Xunit;

namespace IMed.Api.IntegrationTests.Lab;

/// <summary>
/// Verifica que el renderer de etiquetas (R6-F4b) produce PDFs válidos en todo el rango de
/// tamaños que un laboratorio puede configurar — desde una cryovial de 33 × 13 mm hasta un
/// frasco de 76 × 51 mm — y que mezcla tamaños distintos en un mismo PDF.
///
/// <para>Es el test que faltaba: el bug original era que "Imprimir etiqueta" no producía
/// nada. Un renderer que explota con una etiqueta chica reintroduce exactamente ese síntoma.</para>
/// Test puro (sin DB/HAPI/KC).
/// </summary>
public sealed class SpecimenLabelPdfRendererTests
{
    private static SpecimenLabelPage Page(SpecimenLabelFormat format, string accession = "A1B2-20260728-0007") =>
        new(
            Format: format,
            Accession: accession,
            BarcodeValue: accession,
            PatientName: "María Fernanda García López",
            PatientBirthDate: "1988-03-14",
            ContainerLabel: "Tubo EDTA (tapa lila) · EDTA K2/K3",
            Sequence: 1,
            RequiredVolumeMl: 4.0m,
            Tests: "Hemograma completo, Glucosa, Creatinina, Sodio, Potasio",
            OrderNumber: "ORD-20260728-0007",
            PrintedAt: DateTimeOffset.UtcNow);

    public static TheoryData<string, decimal, decimal, decimal> StandardFormats() => new()
    {
        { "Cryovial 33 × 13", 33m, 13m, 0.8m },
        { "Microtainer 38 × 19", 38m, 19m, 1m },
        { "Tubo estándar 51 × 25", 51m, 25m, 1.5m },
        { "Tubo wrap 76 × 25", 76m, 25m, 1.5m },
        { "Frasco 76 × 51", 76m, 51m, 2.5m },
    };

    /// <summary>Tolerancia al comparar milímetros contra los puntos del PDF (redondeo de unidades).</summary>
    private const double ToleranceMm = 0.6;

    private const double MmPerPoint = 25.4 / 72.0;

    /// <summary>
    /// La página debe medir exactamente lo que mide la etiqueta. Es la aserción que importa:
    /// si el PDF sale en A4, la impresora térmica escupe una etiqueta ilegible aunque los
    /// bytes del PDF sean perfectamente válidos.
    /// </summary>
    [Theory]
    [MemberData(nameof(StandardFormats))]
    public void Render_AtEveryStandardSize_ProducesPageOfThatExactSize(
        string name, decimal widthMm, decimal heightMm, decimal marginMm)
    {
        var renderer = new SpecimenLabelPdfRenderer();
        var format = new SpecimenLabelFormat(name, widthMm, heightMm, marginMm, 0m, 0m);

        var bytes = renderer.Render(new SpecimenLabelSheet([Page(format)]));

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 500, $"PDF demasiado pequeño para {name}: {bytes.Length} bytes");

        using var ms = new MemoryStream(bytes);
        using var pdf = PdfReader.Open(ms, PdfDocumentOpenMode.InformationOnly);
        var page = pdf.Pages[0];

        Assert.Equal((double)widthMm, page.Width.Point * MmPerPoint, ToleranceMm);
        Assert.Equal((double)heightMm, page.Height.Point * MmPerPoint, ToleranceMm);
    }

    [Fact]
    public void Render_WithMixedFormats_ProducesOnePageOfEachSize()
    {
        var renderer = new SpecimenLabelPdfRenderer();
        var tube = new SpecimenLabelFormat("Tubo estándar", 51m, 25m, 1.5m, 0m, 0m);
        var cup = new SpecimenLabelFormat("Frasco orina", 76m, 51m, 2.5m, 0m, 0m);

        // Una misma orden puede pedir sangre y orina: el PDF lleva páginas de distinto tamaño.
        var bytes = renderer.Render(new SpecimenLabelSheet(
        [
            Page(tube, "A1B2-20260728-0007"),
            Page(cup, "A1B2-20260728-0008"),
        ]));

        using var ms = new MemoryStream(bytes);
        using var pdf = PdfReader.Open(ms, PdfDocumentOpenMode.InformationOnly);

        Assert.Equal(2, pdf.PageCount);
        Assert.Equal(51d, pdf.Pages[0].Width.Point * MmPerPoint, ToleranceMm);
        Assert.Equal(25d, pdf.Pages[0].Height.Point * MmPerPoint, ToleranceMm);
        Assert.Equal(76d, pdf.Pages[1].Width.Point * MmPerPoint, ToleranceMm);
        Assert.Equal(51d, pdf.Pages[1].Height.Point * MmPerPoint, ToleranceMm);
    }

    /// <summary>La calibración corre el contenido dentro del papel sin romper el render.</summary>
    [Fact]
    public void Render_WithCalibrationOffsets_ProducesPdfBytes()
    {
        var renderer = new SpecimenLabelPdfRenderer();
        var format = new SpecimenLabelFormat("Tubo corrido", 51m, 25m, 1.5m, 1.2m, -0.8m);

        var bytes = renderer.Render(new SpecimenLabelSheet([Page(format)]));

        Assert.True(bytes.Length > 500);
    }

    /// <summary>
    /// Sin paciente ni pruebas (HAPI caído al imprimir) la etiqueta igual sale: el accession y
    /// el barcode son lo que vincula el tubo con la orden.
    /// </summary>
    [Fact]
    public void Render_WithoutPatientData_StillProducesPdf()
    {
        var renderer = new SpecimenLabelPdfRenderer();
        var format = new SpecimenLabelFormat("Tubo estándar", 51m, 25m, 1.5m, 0m, 0m);
        var page = new SpecimenLabelPage(
            format, "A1B2-20260728-0009", "A1B2-20260728-0009",
            PatientName: null, PatientBirthDate: null, ContainerLabel: null,
            Sequence: 2, RequiredVolumeMl: null, Tests: null, OrderNumber: null,
            PrintedAt: DateTimeOffset.UtcNow);

        var bytes = renderer.Render(new SpecimenLabelSheet([page]));

        Assert.True(bytes.Length > 500);
    }

    [Fact]
    public void Render_WithNoLabels_Throws()
    {
        var renderer = new SpecimenLabelPdfRenderer();

        Assert.Throws<ArgumentException>(() => renderer.Render(new SpecimenLabelSheet([])));
    }
}
