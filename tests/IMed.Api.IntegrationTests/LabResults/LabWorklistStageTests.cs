using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabResults;

/// <summary>
/// Etapas de la worklist de Resultados (observación de staging: el filtro "Pendientes" era el
/// estado interno "muestra no recibida", y un paciente con resultados por revisar nunca aparecía).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabWorklistStageTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"]));
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

    private static Task<HttpResponseMessage> EnterAsync(HttpClient client, string srId, decimal value) =>
        client.PostAsJsonAsync("/api/lab/results", new
        {
            serviceRequestId = srId,
            externalLabName = (string?)null,
            results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = value, valueString = (string?)null, unit = "mg/dL" } },
            conclusion = (string?)null,
        });

    private static async Task<List<WorklistItem>> StageAsync(HttpClient client, string stage) =>
        (await client.GetFromJsonAsync<List<WorklistItem>>($"/api/lab/worklist?stage={stage}"))!;

    [Fact]
    public async Task ResultadoPorRevisar_apareceEnPorValidar_yAlLiberarPasaAValidadas()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-etapas");

        (await StageAsync(client, "PendingResults")).Should().ContainSingle(i => i.ServiceRequestId == srId);

        // 600 es crítico: el motor no lo autovalida, queda para revisar.
        var entered = await (await EnterAsync(client, srId, 600m)).Content.ReadFromJsonAsync<EnterResultsResult>();
        entered!.ReportStatus.Should().Be("preliminary");

        (await StageAsync(client, "PendingResults")).Should().NotContain(i => i.ServiceRequestId == srId);
        var porValidar = (await StageAsync(client, "PendingValidation")).Should().ContainSingle(i => i.ServiceRequestId == srId).Subject;
        porValidar.Stage.Should().Be("PendingValidation");

        // La cola de validación ahora dice de quién es el informe.
        var cola = await client.GetFromJsonAsync<List<LabReportSummaryDto>>("/api/lab/results/pending");
        var caso = cola!.Should().ContainSingle(r => r.ReportId == entered.DiagnosticReportId).Subject;
        caso.PatientFhirId.Should().Be("pat-etapas");
        caso.AccessionNumber.Should().Be(porValidar.AccessionNumber);

        (await client.PostAsJsonAsync($"/api/lab/results/{entered.DiagnosticReportId}/release", new { comment = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StageAsync(client, "PendingValidation")).Should().NotContain(i => i.ServiceRequestId == srId);
        (await StageAsync(client, "Validated")).Should().ContainSingle(i => i.ServiceRequestId == srId);
    }

    [Fact]
    public async Task ResultadoAutovalidado_cierraLaOrden_yNoQuedaPorValidar()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-auto");

        var entered = await (await EnterAsync(client, srId, 85m)).Content.ReadFromJsonAsync<EnterResultsResult>();
        entered!.ReportStatus.Should().Be("final", "un valor normal lo autovalida el motor de reglas");

        (await StageAsync(client, "PendingValidation")).Should().NotContain(i => i.ServiceRequestId == srId);
        var validada = (await StageAsync(client, "Validated")).Should().ContainSingle(i => i.ServiceRequestId == srId).Subject;
        validada.Status.Should().Be("Completed");
    }
}
