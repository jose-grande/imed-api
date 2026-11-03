using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IMed.Api.IntegrationTests.LabResults;

/// <summary>
/// Informe de resultados de una orden (observación del cliente): una sola impresión con paneles
/// y pruebas individuales, por área y subárea, solo lo validado, sin títulos vacíos, y lo fuera
/// de rango marcado únicamente con * o **.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrderReportTests(ImedApiFixture fixture) : IAsyncLifetime
{
    private const string TestSystem = "http://imed.local/lab-test";

    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Guarda el modelo que llega al renderer, para verificar la estructura y no los bytes.</summary>
    private sealed class CapturingRenderer : ILabReportPdfRenderer
    {
        public LabReportPdfModel? Last { get; private set; }

        public byte[] Render(LabReportPdfModel model)
        {
            Last = model;
            return "%PDF-1.4"u8.ToArray();
        }
    }

    private sealed record Exam(string? loincCode, string? localCode, string? localSystem, string codeText, string codeDisplay);

    [Fact]
    public async Task InformeDeLaOrden_porAreaYSubarea_soloValidado_conAsteriscos()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"]);

        var renderer = new CapturingRenderer();
        using var host = fixture.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ILabReportPdfRenderer>();
            s.AddSingleton<ILabReportPdfRenderer>(renderer);
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // Una orden: hemograma (panel), glucosa y TSH (individuales).
        var orderResp = await client.PostAsJsonAsync("/api/lab/reception/external-order-multi", new
        {
            patientFhirId = "pat-informe",
            requesterName = "Dr. Externo",
            requesterIdentifier = (string?)null,
            requesterInstitution = (string?)null,
            priority = "routine",
            occurrenceDateTime = (string?)null,
            reasonText = (string?)null,
            note = (string?)null,
            exams = new[]
            {
                new Exam("58410-2", "CBC", "http://imed.local/panel", "Hemograma completo (Biometría hemática)", "Hemograma completo (Biometría hemática)"),
                new Exam("2345-7", "GLU", TestSystem, "Glucosa", "Glucosa"),
                new Exam("3016-3", "TSH", TestSystem, "Hormona estimulante de tiroides (TSH)", "Hormona estimulante de tiroides (TSH)"),
            },
        });
        orderResp.StatusCode.Should().Be(HttpStatusCode.Created, await orderResp.Content.ReadAsStringAsync());
        var created = await orderResp.Content.ReadFromJsonAsync<CreatedExternalOrderMulti>();
        created!.ServiceRequestIds.Should().HaveCount(3);

        var reportIds = new List<string>();
        foreach (var srId in created.ServiceRequestIds)
        {
            await client.PostAsJsonAsync("/api/lab/reception/receive", new
            {
                serviceRequestId = srId, fastingSatisfied = (bool?)null, isWalkIn = true,
                scheduledFor = (DateTimeOffset?)null, typeText = (string?)null, collectedDateTime = (string?)null, note = (string?)null,
            });
            // La TSH se recibe pero no se resulta: queda pendiente. (Un valor normal lo
            // autovalidaría el motor de reglas, y autovalidado cuenta como validado.)
            if (srId == created.ServiceRequestIds[2])
            {
                continue;
            }
            var sheet = (await client.GetFromJsonAsync<ResultEntrySheetDto>($"/api/lab/results/entry-sheet/{srId}"))!;
            var lines = sheet.Analytes.Select(a => new
            {
                loinc = a.LoincCode, codeText = a.Name, labTestId = a.LabTestId, valueString = (string?)null, unit = a.Unit,
                // Glucosa 600 (> crítico 500) y leucocitos 15 (> 11): uno ** y otro *.
                valueNumeric = a.Code switch { "GLU" => 600m, "WBC" => 15m, _ => (a.ReferenceLow ?? 1m) },
            }).ToArray();
            var saved = await client.PostAsJsonAsync("/api/lab/results",
                new { serviceRequestId = srId, externalLabName = (string?)null, results = lines, conclusion = (string?)null });
            saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            reportIds.Add((await saved.Content.ReadFromJsonAsync<EnterResultsResult>())!.DiagnosticReportId);
        }

        // Se validan el hemograma y la glucosa; la TSH queda pendiente.
        foreach (var id in reportIds)
        {
            await client.PostAsJsonAsync($"/api/lab/results/{id}/release", new { comment = (string?)null });
        }

        // Una sola impresión: se pide por el informe de la glucosa y sale la orden completa.
        var pdf = await client.GetAsync($"/api/lab/results/{reportIds[1]}/pdf");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK, await pdf.Content.ReadAsStringAsync());
        var model = renderer.Last!;

        model.PendingCount.Should().Be(1, "la TSH no se validó");
        model.Areas.Select(a => a.Title).Should().Equal(["Hematología", "Química clínica"],
            "Inmunología solo tenía la TSH sin validar: el área completa desaparece");

        var hemato = model.Areas[0];
        var hemograma = hemato.Blocks.Should().ContainSingle().Subject;
        hemograma.Title.Should().Be("Hemograma completo (Biometría hemática)");
        hemograma.Subsections.Select(s => s.Title).Should().Equal(["Serie roja", "Serie blanca", "Serie plaquetaria"]);
        hemograma.Subsections.Should().OnlyContain(s => s.Lines.Count > 0, "no hay subáreas vacías");
        hemograma.Subsections[1].Lines.Single(l => l.Name.StartsWith("Leucocitos", StringComparison.Ordinal))
            .Mark.Should().Be("*");

        var quimica = model.Areas[1].Blocks.Should().ContainSingle().Subject;
        quimica.Title.Should().BeNull("una prueba individual va suelta en su área");
        var glucosa = quimica.Subsections.Single().Lines.Single();
        glucosa.Name.Should().Be("Glucosa");
        glucosa.Mark.Should().Be("**");
        glucosa.Value.Should().Be("600");
    }

    [Theory]
    [InlineData("HH", "**")]
    [InlineData("LL", "**")]
    [InlineData("H", "*")]
    [InlineData("L", "*")]
    [InlineData("A", "*")]
    [InlineData("N", "")]
    [InlineData(null, "")]
    public void Marca_soloAsteriscos(string? flag, string marca) =>
        GetLabReportPdfHandler.MarkOf(flag).Should().Be(marca);
}
