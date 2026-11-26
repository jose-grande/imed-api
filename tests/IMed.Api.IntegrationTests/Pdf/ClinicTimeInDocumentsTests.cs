using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Pdf;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Pdf;

/// <summary>
/// Toda fecha y hora impresa sale en la hora de la clínica. Antes cada renderer hacía
/// <c>ToLocalTime()</c>, que usa la zona del servidor: en staging (UTC) la factura, la orden, el
/// informe y las etiquetas salían con seis horas de más, y después de las 18:00 con la fecha de
/// mañana. El texto del PDF va codificado, así que se prueba que cada fecha pase por el reloj de la
/// clínica (que 20:30 UTC sea 14:30 lo prueba <c>PrescriptionPdfTests</c>).
/// </summary>
public sealed class ClinicTimeInDocumentsTests
{
    private static readonly DateTimeOffset Emision = new(2026, 11, 27, 1, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Recepcion = new(2026, 11, 26, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Liberacion = new(2026, 11, 27, 0, 15, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FacturaOriginal = new(2026, 11, 20, 23, 45, 0, TimeSpan.Zero);

    private static readonly PdfOrgInfo Org = new("Clínica Nelson", null);
    private static readonly PdfPatientInfo Paciente = new("Juan Pérez", "1990-01-01", "male", "DNI", "0801-1990-12345");
    private static readonly PdfPractitionerInfo Medico = new("Nelson Pérez", "Dr.", "12345", "Medicina general");

    [Fact]
    public void LaFactura_imprimeEmisionYFacturaModificadaEnHoraDeLaClinica()
    {
        var reloj = new RecordingClinicClock();
        var modelo = new InvoicePdfModel(
            new PdfIssuerInfo("Clínica Nelson S. de R.L.", "08019999123456", "Tegucigalpa", "2200-0000"),
            new PdfBuyerInfo("Juan Pérez", "08011990123456"),
            new PdfFiscalInfo("001-002-03-00000001", "Nota de crédito", Emision,
                "A1B2C3-D4E5F6-A7B8C9", "1-500", new DateOnly(2027, 12, 31), false),
            [new PdfInvoiceLine(1, "Consulta general", 1m, 500m, 0m, 500m, "E")],
            new PdfInvoiceTotals(500m, 0m, 0m, 0m, 0m, 0m, 500m, "QUINIENTOS LEMPIRAS CON 00/100"),
            Modifies: new PdfModifiedDocument("001-002-01-00000009", "A1B2C3-D4E5F6-A7B8C9", FacturaOriginal));

        new InvoicePdfRenderer(reloj).Render(modelo, InvoicePrintFormat.Letter);
        new InvoicePdfRenderer(reloj).Render(modelo, InvoicePrintFormat.Thermal80);

        reloj.Converted.Should().Contain([Emision, FacturaOriginal]);
    }

    [Fact]
    public void LaOrdenDeLaboratorio_imprimeLaEmisionEnHoraDeLaClinica()
    {
        var reloj = new RecordingClinicClock();
        var modelo = new LabOrderPdfModel(
            Organization: Org, Practitioner: Medico, Patient: Paciente,
            Items: [new PdfLabOrderItem("Hemograma completo", "58410-2", "Rutina", null, null, null, "Disponible en la clínica")],
            IssuedAt: Emision, OrderNumber: "ORD-20261126-0007");

        new LabOrderPdfRenderer(reloj).Render(modelo);

        reloj.Converted.Should().Contain(Emision);
    }

    [Fact]
    public void ElInforme_imprimeRecepcionLiberacionEImpresionEnHoraDeLaClinica()
    {
        var reloj = new RecordingClinicClock();
        var modelo = new LabReportPdfModel(
            Organization: Org, Patient: Paciente, Areas: [],
            ReceivedAt: Recepcion, ReleasedAt: Liberacion, PrintedAt: Emision);

        new LabReportPdfRenderer(reloj).Render(modelo);

        reloj.Converted.Should().Contain([Recepcion, Liberacion, Emision]);
    }

    [Fact]
    public void LasEtiquetas_imprimenLaHoraDeLaClinica()
    {
        var reloj = new RecordingClinicClock();
        var etiqueta = new SpecimenLabelPage(
            new SpecimenLabelFormat("Tubo 50 × 25", 50m, 25m, 1.5m, 0m, 0m),
            "A1B2-20261126-0007", "A1B2-20261126-0007", "Juan Pérez", "1990-01-01",
            "Tubo EDTA (tapa lila)", 1, 3m, "Hemograma", "ORD-20261126-0007", Emision);

        new SpecimenLabelPdfRenderer(reloj).Render(new SpecimenLabelSheet([etiqueta]));

        reloj.Converted.Should().Contain(Emision);
    }
}
