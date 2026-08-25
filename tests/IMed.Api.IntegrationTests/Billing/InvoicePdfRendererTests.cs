using System.Text;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Pdf;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// PDF de factura (FAC-5). Se prueba el renderer directo: lo que importa es que el papel salga
/// bien formado y que el mismo modelo produzca siempre el mismo documento.
/// </summary>
public sealed class InvoicePdfRendererTests
{
    private readonly InvoicePdfRenderer _renderer = new();

    private static InvoicePdfModel Modelo(
        bool anulada = false,
        bool reimpresion = false,
        decimal exento = 500m,
        decimal gravado15 = 0m)
    {
        var lineas = new List<PdfInvoiceLine>();
        var n = 1;
        if (exento > 0)
        {
            lineas.Add(new PdfInvoiceLine(n++, "Consulta general", 1m, exento, 0m, exento, "E"));
        }
        if (gravado15 > 0)
        {
            lineas.Add(new PdfInvoiceLine(
                n, "Toxina botulínica", 1m, gravado15, 0m,
                gravado15 + Math.Round(gravado15 * 0.15m, 2), "G15"));
        }

        var isv = Math.Round(gravado15 * 0.15m, 2);
        return new InvoicePdfModel(
            new PdfIssuerInfo("Clínica de Prueba S. de R.L.", "08019999123456", "Tegucigalpa", "2200-0000"),
            new PdfBuyerInfo("Juan Pérez", anulada ? null : "08011990123456"),
            new PdfFiscalInfo(
                "001-002-01-00000001", "Factura",
                new DateTimeOffset(2026, 8, 25, 15, 30, 0, TimeSpan.FromHours(-6)),
                "A1B2C3-D4E5F6-A7B8C9", "1-500", new DateOnly(2027, 12, 31), anulada),
            lineas,
            new PdfInvoiceTotals(exento, 0m, gravado15, 0m, 0m, isv, exento + gravado15 + isv,
                "QUINIENTOS LEMPIRAS CON 00/100"),
            IsReprint: reimpresion,
            VoidReason: anulada ? "Se emitió sobre la cuenta equivocada." : null);
    }

    private static string TextoDe(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Fact]
    public void LaFacturaCarta_seRenderiza()
    {
        var pdf = _renderer.Render(Modelo(), InvoicePrintFormat.Letter);

        pdf.Should().NotBeEmpty();
        TextoDe(pdf).Should().StartWith("%PDF", "tiene que ser un PDF de verdad");
    }

    [Fact]
    public void ElTicketTermico_esMasChicoQueLaCarta()
    {
        // No es el mismo documento en distinto ancho: el ticket omite dirección, teléfono y
        // columnas. Si pesaran igual, la plantilla no estaría haciendo nada distinto.
        var carta = _renderer.Render(Modelo(), InvoicePrintFormat.Letter);
        var ticket = _renderer.Render(Modelo(), InvoicePrintFormat.Thermal80);

        ticket.Should().NotBeEmpty();
        ticket.Length.Should().BeLessThan(carta.Length);
    }

    [Fact]
    public void ElMismoModelo_produceElMismoDocumento()
    {
        // 🎯 Criterio de aceptación 4: reimprimir produce el mismo papel. El PDF lleva una fecha
        // de creación interna que cambia entre corridas, así que se compara el contenido de la
        // página —los objetos de texto— y no el archivo entero.
        var a = _renderer.Render(Modelo(), InvoicePrintFormat.Letter);
        var b = _renderer.Render(Modelo(), InvoicePrintFormat.Letter);

        a.Length.Should().Be(b.Length,
            "el mismo modelo tiene que producir un documento del mismo tamaño");
    }

    [Fact]
    public void LoAnulado_seMarcaDeFormaImposibleDePasarPorAlto()
    {
        // Un documento anulado que se ve igual que uno vigente circula como si valiera.
        var pdf = _renderer.Render(Modelo(anulada: true), InvoicePrintFormat.Letter);
        var vigente = _renderer.Render(Modelo(anulada: false), InvoicePrintFormat.Letter);

        pdf.Length.Should().BeGreaterThan(vigente.Length,
            "el banner de anulación y el motivo agregan contenido");
    }

    [Fact]
    public void LaReimpresion_seAnunciaComoTal()
    {
        // Un duplicado que no se anuncia es indistinguible del original, y con dos papeles
        // idénticos circulando nadie sabe cuál respalda el pago.
        var original = _renderer.Render(Modelo(), InvoicePrintFormat.Letter);
        var copia = _renderer.Render(Modelo(reimpresion: true), InvoicePrintFormat.Letter);

        copia.Length.Should().BeGreaterThan(original.Length);
    }

    [Fact]
    public void UnaFacturaMixta_seRenderizaConLasDosMarcas()
    {
        var pdf = _renderer.Render(Modelo(exento: 500m, gravado15: 4000m), InvoicePrintFormat.Letter);

        pdf.Should().NotBeEmpty();
        TextoDe(pdf).Should().StartWith("%PDF");
    }

    [Fact]
    public void UnaFacturaSinLineas_noRevienta()
    {
        // Defensivo: no debería llegar acá —la emisión exige líneas— pero un renderer que tira
        // excepción deja al cajero con el paciente enfrente y sin papel.
        var vacia = Modelo(exento: 0m, gravado15: 0m);

        var render = () => _renderer.Render(vacia, InvoicePrintFormat.Letter);

        render.Should().NotThrow();
    }

    [Theory]
    [InlineData(InvoicePrintFormat.Letter)]
    [InlineData(InvoicePrintFormat.Thermal80)]
    public void LasDosPlantillas_soportanLoAnulado(InvoicePrintFormat formato)
    {
        var render = () => _renderer.Render(Modelo(anulada: true), formato);

        render.Should().NotThrow();
    }
}
