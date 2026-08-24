using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabDashboard;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabDashboard;

/// <summary>Tests del dashboard operacional (Épica LIS-8, F3). Org aislada por test.</summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabDashboardTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        // Catálogo estricto por org (copy-on-create, rev 53): sin aprovisionar, las pruebas no
        // resuelven, la orden se enruta a externo y el dashboard cuenta cero.
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"]);
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

    private static async Task<EnterResultsResult> EnterGlucoseAsync(HttpClient client, string srId, decimal value) =>
        (await (await client.PostAsJsonAsync("/api/lab/results", new
        {
            serviceRequestId = srId, externalLabName = (string?)null,
            results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = value, valueString = (string?)null, unit = "mg/dL" } },
            conclusion = (string?)null,
        })).Content.ReadFromJsonAsync<EnterResultsResult>())!;

    [Fact]
    public async Task Dashboard_cuentaRecibidasValidacionYCriticos()
    {
        var client = await AuthedClient();
        await ReceiveGlucoseAsync(client, "pat-a");                       // recibida, sin resultado
        var srB = await ReceiveGlucoseAsync(client, "pat-b");
        await EnterGlucoseAsync(client, srB, 600m);                       // crítico → preliminary

        var d = await (await client.GetAsync("/api/lab/dashboard")).Content.ReadFromJsonAsync<LabDashboardDto>();
        d!.ReceivedToday.Should().Be(2);
        d.InLab.Should().Be(2, "ambas siguen en el laboratorio (Received)");
        d.AwaitingValidation.Should().Be(1, "el resultado 600 quedó preliminary");
        d.CriticalToday.Should().Be(1, "CRITICAL_FLAG disparó sobre 600");
        d.ReleasedToday.Should().Be(0);
    }

    [Fact]
    public async Task Dashboard_reflejaLiberacion()
    {
        var client = await AuthedClient();
        var sr = await ReceiveGlucoseAsync(client, "pat-c");
        var entered = await EnterGlucoseAsync(client, sr, 200m);          // H → preliminary
        await client.PostAsJsonAsync($"/api/lab/results/{entered.DiagnosticReportId}/release", new { });

        var d = await (await client.GetAsync("/api/lab/dashboard")).Content.ReadFromJsonAsync<LabDashboardDto>();
        d!.ReleasedToday.Should().Be(1, "se liberó una");
        d.InLab.Should().Be(0, "ya no está Received (pasó a Completed)");
        d.AwaitingValidation.Should().Be(0, "el informe pasó a final");
    }
}
