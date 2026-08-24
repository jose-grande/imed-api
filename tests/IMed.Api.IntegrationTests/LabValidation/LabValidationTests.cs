using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabValidation;

/// <summary>
/// Tests de validación/liberación (Épica LIS-6, F4): pendientes, liberación
/// (→ final + intake Completed), historial, RBAC.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabValidationTests(ImedApiFixture fixture) : IAsyncLifetime
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

    private static async Task<string> ReceiveGlucoseAsync(HttpClient client, string patientId)
    {
        var created = await (await client.PostAsJsonAsync("/api/lab/reception/external-order", new
        {
            patientFhirId = patientId, requesterName = "Dr. Externo", requesterIdentifier = (string?)null,
            loincCode = "2345-7", codeText = "Glucosa", priority = "routine",
            occurrenceDateTime = (string?)null, reasonText = (string?)null, note = (string?)null,
        })).Content.ReadFromJsonAsync<CreatedExternalOrder>();
        await client.PostAsJsonAsync("/api/lab/reception/receive", new
        {
            serviceRequestId = created!.ServiceRequestId, fastingSatisfied = (bool?)null, isWalkIn = true,
            scheduledFor = (DateTimeOffset?)null, typeText = (string?)null, collectedDateTime = (string?)null, note = (string?)null,
        });
        return created.ServiceRequestId;
    }

    private static async Task<EnterResultsResult> EnterGlucoseAsync(HttpClient client, string srId, decimal value)
    {
        var resp = await client.PostAsJsonAsync("/api/lab/results", new
        {
            serviceRequestId = srId, externalLabName = (string?)null,
            results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = value, valueString = (string?)null, unit = "mg/dL" } },
            conclusion = (string?)null,
        });
        return (await resp.Content.ReadFromJsonAsync<EnterResultsResult>())!;
    }

    [Fact]
    public async Task Pending_listaInformePreliminary()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-pend");
        var entered = await EnterGlucoseAsync(client, srId, 200m); // H → no auto-valida → preliminary
        entered.ReportStatus.Should().Be("preliminary");

        var pending = await (await client.GetAsync("/api/lab/results/pending")).Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        pending!.Should().Contain(p => p.ReportId == entered.DiagnosticReportId);
    }

    [Fact]
    public async Task Release_marcaFinal_yDesapareceDePendientes()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-rel");
        var entered = await EnterGlucoseAsync(client, srId, 200m);

        var rel = await client.PostAsJsonAsync($"/api/lab/results/{entered.DiagnosticReportId}/release", new { });
        rel.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await rel.Content.ReadFromJsonAsync<ReleaseReportResult>();
        result!.Status.Should().Be("final");

        var detail = await (await client.GetAsync($"/api/lab/results/{entered.DiagnosticReportId}")).Content.ReadFromJsonAsync<LabReportDetailDto>();
        detail!.Status.Should().Be("final");

        var pending = await (await client.GetAsync("/api/lab/results/pending")).Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        pending!.Should().NotContain(p => p.ReportId == entered.DiagnosticReportId);
    }

    [Fact]
    public async Task Release_rolInsuficiente_retorna403()
    {
        var client = await AuthedClient("lab_tech"); // LabTechnician, no LabValidator
        var resp = await client.PostAsJsonAsync("/api/lab/results/dr-x/release", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task History_devuelveValoresPrevios()
    {
        var client = await AuthedClient();
        var sr1 = await ReceiveGlucoseAsync(client, "pat-hist");
        await EnterGlucoseAsync(client, sr1, 85m);
        var sr2 = await ReceiveGlucoseAsync(client, "pat-hist");
        var entered2 = await EnterGlucoseAsync(client, sr2, 200m);

        var history = await (await client.GetAsync($"/api/lab/results/{entered2.DiagnosticReportId}/history"))
            .Content.ReadFromJsonAsync<HistoryPointDto[]>();
        history!.Should().Contain(h => h.Value == 85m);
    }
}
