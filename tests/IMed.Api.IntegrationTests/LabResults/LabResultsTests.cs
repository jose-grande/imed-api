using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabResults;

/// <summary>
/// Tests de captura/lectura de resultados (Épica LIS-4, F5). Flujo completo:
/// crear orden externa → recibir (in-house) → ingresar resultados → leer.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabResultsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: Guid.NewGuid().ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    /// <summary>Crea orden externa GLU + la recibe in-house; devuelve el serviceRequestId.</summary>
    private static async Task<string> ReceiveGlucoseAsync(HttpClient client, string patientId)
    {
        var created = await (await client.PostAsJsonAsync("/api/lab/reception/external-order", new
        {
            patientFhirId = patientId, requesterName = "Dr. Externo", requesterIdentifier = (string?)null,
            loincCode = "2345-7", codeText = "Glucosa", priority = "routine",
            occurrenceDateTime = (string?)null, reasonText = (string?)null, note = (string?)null,
        })).Content.ReadFromJsonAsync<CreatedExternalOrder>();

        var rec = await client.PostAsJsonAsync("/api/lab/reception/receive", new
        {
            serviceRequestId = created!.ServiceRequestId, fastingSatisfied = (bool?)null, isWalkIn = true,
            scheduledFor = (DateTimeOffset?)null, typeText = (string?)null, collectedDateTime = (string?)null, note = (string?)null,
        });
        (await rec.Content.ReadFromJsonAsync<ReceiveResult>())!.Status.Should().Be("Received");
        return created.ServiceRequestId;
    }

    private static object ResultsBody(string srId, decimal value) => new
    {
        serviceRequestId = srId,
        externalLabName = (string?)null,
        results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = value, valueString = (string?)null, unit = "mg/dL" } },
        conclusion = (string?)null,
    };

    [Fact]
    public async Task EnterResults_valorCritico_flagHH_yHasCritical()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-crit");

        var resp = await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 600m));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<EnterResultsResult>();
        result!.Origin.Should().Be("InHouse");
        result.HasCritical.Should().BeTrue();
        result.Observations.Should().ContainSingle().Which.FlagCode.Should().Be("HH", "600 > crítico alto 500");
    }

    [Fact]
    public async Task EnterResults_valorNormal_flagN()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-norm");

        var result = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 85m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();
        result!.HasCritical.Should().BeFalse();
        result.Observations.Should().ContainSingle().Which.FlagCode.Should().Be("N");
    }

    [Fact]
    public async Task EnterResults_ordenNoRecibida_retorna409()
    {
        var client = await AuthedClient();
        // Crear orden pero NO recibir → sin intake.
        var created = await (await client.PostAsJsonAsync("/api/lab/reception/external-order", new
        {
            patientFhirId = "pat-x", requesterName = "Dr. Externo", requesterIdentifier = (string?)null,
            loincCode = "2345-7", codeText = "Glucosa", priority = "routine",
            occurrenceDateTime = (string?)null, reasonText = (string?)null, note = (string?)null,
        })).Content.ReadFromJsonAsync<CreatedExternalOrder>();

        var resp = await client.PostAsJsonAsync("/api/lab/results", ResultsBody(created!.ServiceRequestId, 85m));
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task EnterResults_rolInsuficiente_retorna403()
    {
        // lab_tech tiene LabTechnician pero NO LabValidator.
        var client = await AuthedClient("lab_tech");
        var resp = await client.PostAsJsonAsync("/api/lab/results", ResultsBody("sr-x", 85m));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadResults_byPatient_yDetalle()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-read");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();

        var summaries = await (await client.GetAsync("/api/lab/results/by-patient/pat-read"))
            .Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        summaries!.Should().Contain(s => s.ReportId == entered!.DiagnosticReportId);

        var detail = await (await client.GetAsync($"/api/lab/results/{entered!.DiagnosticReportId}"))
            .Content.ReadFromJsonAsync<LabReportDetailDto>();
        detail!.Observations.Should().ContainSingle();
        detail.Observations[0].FlagCode.Should().Be("H", "150 > alto 99");
        detail.Observations[0].ValueNumeric.Should().Be(150m);
    }
}
