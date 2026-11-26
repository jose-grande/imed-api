using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabDashboard;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>Mueve los relojes de una orden: minutos hacia atrás desde ahora (null = no tocar).</summary>
    private async Task SetClocksAsync(string srId, int receivedAgo, int? validatedAgo, int updatedAgo = 0)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE order_intake SET
              received_at = now() - make_interval(mins => {receivedAgo}),
              validated_at = CASE WHEN {validatedAgo}::int IS NULL THEN validated_at ELSE now() - make_interval(mins => {validatedAgo}::int) END,
              updated_at = now() - make_interval(mins => {updatedAgo})
            WHERE service_request_fhir_id = {srId}
            """);
    }

    private static async Task<string> ReleaseGlucoseAsync(HttpClient client, string patientId)
    {
        var sr = await ReceiveGlucoseAsync(client, patientId);
        var entered = await EnterGlucoseAsync(client, sr, 200m);          // H → preliminary
        await client.PostAsJsonAsync($"/api/lab/results/{entered.DiagnosticReportId}/release", new { });
        return sr;
    }

    [Fact]
    public async Task Dashboard_tatEsDeLoLiberadoHoy_medidoHastaLaLiberacion()
    {
        // Observación: el TAT no reflejaba el dato correcto. Medía hasta updated_at (que se mueve
        // por cualquier cosa) y promediaba todo lo liberado desde siempre.
        var client = await AuthedClient();
        var hoy = await ReleaseGlucoseAsync(client, "pat-hoy");
        var antes = await ReleaseGlucoseAsync(client, "pat-antes");

        // Hoy: recibida hace 45 min, liberada hace 5 → TAT 40. Algo la tocó después (updated_at = ahora).
        await SetClocksAsync(hoy, receivedAgo: 45, validatedAgo: 5, updatedAgo: 0);
        // Hace tres días, con un TAT de 500 min: no es de hoy.
        await SetClocksAsync(antes, receivedAgo: 3 * 1440 + 500, validatedAgo: 3 * 1440, updatedAgo: 0);

        var d = await (await client.GetAsync("/api/lab/dashboard")).Content.ReadFromJsonAsync<LabDashboardDto>();
        d!.ReleasedToday.Should().Be(1, "la de hace tres días no se liberó hoy aunque updated_at sea de hoy");
        d.TatAverageMinutes.Should().Be(40, "recepción → liberación, no recepción → última modificación");
        d.TatBreaches.Should().Be(0, "40 min está dentro del objetivo de rutina (60)");
    }

    [Fact]
    public async Task Worklist_elTatDeLoLiberadoNoSigueCorriendo()
    {
        var client = await AuthedClient();
        var liberada = await ReleaseGlucoseAsync(client, "pat-liberada");
        var enCurso = await ReceiveGlucoseAsync(client, "pat-en-curso");

        // Liberada hace dos días, 20 min después de recibirse.
        await SetClocksAsync(liberada, receivedAgo: 2 * 1440 + 20, validatedAgo: 2 * 1440);
        await SetClocksAsync(enCurso, receivedAgo: 90, validatedAgo: null);

        var rows = (await client.GetFromJsonAsync<List<WorklistItem>>("/api/lab/worklist"))!;
        var l = rows.Should().ContainSingle(r => r.ServiceRequestId == liberada).Subject;
        l.ElapsedMinutes.Should().Be(20, "el reloj se detiene al liberar");
        l.TatClosed.Should().BeTrue();
        l.TatBreached.Should().BeFalse();

        var c = rows.Should().ContainSingle(r => r.ServiceRequestId == enCurso).Subject;
        c.ElapsedMinutes.Should().BeInRange(90, 91, "lo que sigue en el laboratorio corre hasta ahora");
        c.TatClosed.Should().BeFalse();
        c.TatBreached.Should().BeTrue("90 min supera el objetivo de rutina (60)");
    }
}
