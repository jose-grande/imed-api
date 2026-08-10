using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.Application.LabRules;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabRules;

/// <summary>
/// Tests del motor de reglas integrado (Épica LIS-5, F5): auto-validación,
/// valor crítico, delta check. Usa las reglas globales sembradas (DELTA_CHECK,
/// CRITICAL_FLAG) + el catálogo (GLU 2345-7, rango 70–99, crítico 40–500).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class RuleEngineTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(),
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

    private static Task<HttpResponseMessage> EnterGlucoseAsync(HttpClient client, string srId, decimal value) =>
        client.PostAsJsonAsync("/api/lab/results", new
        {
            serviceRequestId = srId, externalLabName = (string?)null,
            results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = value, valueString = (string?)null, unit = "mg/dL" } },
            conclusion = (string?)null,
        });

    [Fact]
    public async Task ResultadoNormal_autoValida_quedaFinal()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-av-1");

        var result = await (await EnterGlucoseAsync(client, srId, 85m)).Content.ReadFromJsonAsync<EnterResultsResult>();
        result!.ReportStatus.Should().Be("final", "85 es normal → auto-validación");
        result.HasCritical.Should().BeFalse();
        result.RequiresReview.Should().BeFalse();
        result.Observations.Should().ContainSingle().Which.AutoValidated.Should().BeTrue();
    }

    [Fact]
    public async Task ValorCritico_noAutoValida_quedaPreliminary()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-av-2");

        var result = await (await EnterGlucoseAsync(client, srId, 600m)).Content.ReadFromJsonAsync<EnterResultsResult>();
        result!.HasCritical.Should().BeTrue("600 > crítico 500");
        result.ReportStatus.Should().Be("preliminary", "los críticos no se auto-validan");
        result.Observations[0].AutoValidated.Should().BeFalse();
    }

    [Fact]
    public async Task DeltaCheck_cambioGrande_requiereRevision()
    {
        var client = await AuthedClient();

        // Primer resultado (85) para el paciente → queda como histórico.
        var sr1 = await ReceiveGlucoseAsync(client, "pat-delta");
        await EnterGlucoseAsync(client, sr1, 85m);

        // Segunda orden, mismo paciente, valor con cambio >20% (200 vs 85 = +135%).
        var sr2 = await ReceiveGlucoseAsync(client, "pat-delta");
        var result = await (await EnterGlucoseAsync(client, sr2, 200m)).Content.ReadFromJsonAsync<EnterResultsResult>();

        result!.RequiresReview.Should().BeTrue("delta >20% dispara DELTA_CHECK → revisión");
        result.ReportStatus.Should().Be("preliminary");
        result.Observations[0].AutoValidated.Should().BeFalse();
    }

    [Fact]
    public async Task ListRules_traeSeedGlobal()
    {
        var client = await AuthedClient();
        var rules = await (await client.GetAsync("/api/lab-catalog/rules")).Content.ReadFromJsonAsync<LabRuleDto[]>();
        rules!.Should().Contain(r => r.Code == "DELTA_CHECK");
        rules!.Should().Contain(r => r.Code == "CRITICAL_FLAG");
    }

    [Fact]
    public async Task CreateRule_yApareceEnLista()
    {
        var client = await AuthedClient();
        var create = await client.PostAsJsonAsync("/api/lab-catalog/rules", new
        {
            code = "GLU_PANIC", name = "Glucosa muy alta", scope = "Test", targetLoinc = "2345-7",
            action = "FlagCritical", severity = "Critical", message = "Glucosa crítica",
            logicJson = "{\">\":[{\"var\":\"value\"},450]}",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var rules = await (await client.GetAsync("/api/lab-catalog/rules")).Content.ReadFromJsonAsync<LabRuleDto[]>();
        rules!.Should().Contain(r => r.Code == "GLU_PANIC");
    }

    [Fact]
    public async Task CreateRule_rolInsuficiente_403()
    {
        var client = await AuthedClient("bioanalyst"); // LabRead/LabValidator pero no LabSupervisor
        var resp = await client.PostAsJsonAsync("/api/lab-catalog/rules", new
        {
            code = "X", name = "X", scope = "Global", targetLoinc = (string?)null,
            action = "Alert", severity = "Info", message = (string?)null, logicJson = "{}",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
