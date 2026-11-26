using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Patients;
using IMed.Api.Infrastructure.Pdf;
using IMed.Api.Infrastructure.Time;
using Microsoft.Extensions.Options;
using Xunit;

namespace IMed.Api.IntegrationTests.Prescriptions;

/// <summary>
/// Observación de los médicos sobre la receta impresa: falta la dirección de la sede, quieren la
/// edad del paciente, y la hora salía con seis horas de más (se convertía con la zona del servidor,
/// UTC, en vez de la de la clínica). Pruebas puras, sin DB/HAPI/KC.
/// </summary>
public sealed class PrescriptionPdfTests
{
    private static readonly ClinicClock Reloj = new(
        TimeProvider.System, Options.Create(new ClinicClockOptions { TimeZone = "America/Tegucigalpa" }));

    [Fact]
    public void LaHoraImpresa_esLaDeLaClinica_noLaDelServidor()
    {
        // 20:30 UTC son las 14:30 en Honduras (UTC-6, sin horario de verano).
        var emitida = new DateTimeOffset(2026, 11, 26, 20, 30, 0, TimeSpan.Zero);

        var local = Reloj.ToClinicTime(emitida);

        local.Offset.Should().Be(TimeSpan.FromHours(-6));
        PrescriptionPdfRenderer.FormatIssued(local).Should().Be("26/11/2026 14:30");
    }

    [Fact]
    public void CercaDeMedianoche_laFechaTambienEsLaLocal()
    {
        // 03:00 UTC del 27 todavía es el 26 a las 21:00 en la clínica.
        var local = Reloj.ToClinicTime(new DateTimeOffset(2026, 11, 27, 3, 0, 0, TimeSpan.Zero));

        PrescriptionPdfRenderer.FormatIssued(local).Should().Be("26/11/2026 21:00");
    }

    [Theory]
    [InlineData("1990-11-27", "2026-11-26", "35 años")]
    [InlineData("1990-11-26", "2026-11-26", "36 años")]
    [InlineData("2025-11-26", "2026-11-26", "1 año")]
    [InlineData("2026-03-10", "2026-11-26", "8 meses")]
    [InlineData("2026-10-26", "2026-11-26", "1 mes")]
    [InlineData("2026-11-14", "2026-11-26", "12 días")]
    [InlineData("2026-11-26", "2026-11-26", "0 días")]
    [InlineData("1990", "2026-11-26", "36 años")]
    [InlineData("1990-12", "2026-11-26", "35 años")]
    public void LaEdad_seLeeEnAniosMesesODias(string nacimiento, string hoy, string esperado)
    {
        PatientAge.Format(nacimiento, DateOnly.Parse(hoy, System.Globalization.CultureInfo.InvariantCulture))
            .Should().Be(esperado);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no es fecha")]
    [InlineData("2027-01-01")]
    public void SinFechaUtil_noHayEdad(string? nacimiento)
    {
        PatientAge.Format(nacimiento, new DateOnly(2026, 11, 26)).Should().BeNull();
    }

    [Fact]
    public void LaReceta_conSedeYEdad_seGenera()
    {
        var model = new PrescriptionPdfModel(
            Organization: new PdfOrgInfo("Clínica Nelson", null),
            Practitioner: new PdfPractitionerInfo("Nelson Pérez", "Dr.", "12345", "Medicina general"),
            Patient: new PdfPatientInfo("Juan Pérez", "1990-01-01", "male", "DNI", "0801-1990-12345", Age: "36 años"),
            Items: [new PdfMedicationItem("Amoxicilina 500 mg", "1 cápsula c/8 h por 7 días", "Oral", "21", null)],
            IssuedAt: Reloj.ToClinicTime(DateTimeOffset.UtcNow),
            Location: new PdfLocationInfo("Sede Centro", "Col. Palmira, Av. República de Chile", "2232-0000"));

        var bytes = new PrescriptionPdfRenderer().Render(model);

        bytes.Length.Should().BeGreaterThan(500);
    }
}
