using IMed.Api.Application.Abstractions;
using IMed.Api.Infrastructure.Pdf;
using Xunit;

namespace IMed.Api.IntegrationTests.Lab;

/// <summary>
/// Verifica que el renderer del PDF de orden de lab produce bytes válidos con el
/// número de orden + barcode Code128 (F3) — el barcode nunca debe romper el PDF.
/// Test puro (sin DB/HAPI/KC).
/// </summary>
public sealed class LabOrderPdfRendererTests
{
    [Fact]
    public void Render_WithOrderNumberAndBarcode_ProducesPdfBytes()
    {
        var renderer = new LabOrderPdfRenderer();
        var model = new LabOrderPdfModel(
            Organization: new PdfOrgInfo("Clínica Test", null),
            Practitioner: new PdfPractitionerInfo("Test Médico", "Dr.", "12345", "Medicina general"),
            Patient: new PdfPatientInfo("Juan Pérez", "1990-01-01", "male", "DNI", "0801-1990-12345"),
            Items: new[]
            {
                new PdfLabOrderItem("Hemograma completo", "58410-2", "Rutina", null, "control", "ayuno 8h", "Disponible en la clínica"),
                new PdfLabOrderItem("Glucosa", "2345-7", "Urgente", null, null, null, "Referir a laboratorio externo"),
            },
            IssuedAt: DateTimeOffset.UtcNow,
            OrderNumber: "ORD-20260626-0007");

        var bytes = renderer.Render(model);

        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 500, $"PDF demasiado pequeño: {bytes.Length} bytes");
    }
}
