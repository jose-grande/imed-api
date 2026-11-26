using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.LabResults;

/// <summary>
/// Observación de staging: los técnicos piden poder modificar los resultados mientras no se
/// liberen. El técnico captura y corrige (<c>lab.operate</c>), reabriendo la captura completa
/// con un motivo; liberar sigue siendo del validador.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AmendResultsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Org(Guid OrgId, HttpClient Admin);

    private async Task<Org> OrganizacionAsync()
    {
        await Task.Delay(800);
        var orgId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(orgId);
        return new Org(orgId, Cliente(orgId, "org_admin"));
    }

    private HttpClient Cliente(Guid orgId, params string[] roles)
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: roles));
    }

    /// <summary>Orden de glucosa recibida in-house; devuelve el serviceRequestId.</summary>
    private static async Task<string> GlucosaRecibidaAsync(HttpClient client, string patientId)
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

    private static object Captura(string srId, decimal valor) => new
    {
        serviceRequestId = srId,
        externalLabName = (string?)null,
        results = new[] { new { loinc = "2345-7", codeText = "Glucosa", valueNumeric = valor, valueString = (string?)null, unit = "mg/dL" } },
        conclusion = (string?)null,
    };

    private static async Task<EnterResultsResult> CapturarAsync(HttpClient client, string srId, decimal valor)
    {
        var resp = await client.PostAsJsonAsync("/api/lab/results", Captura(srId, valor));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<EnterResultsResult>())!;
    }

    private static object Enmienda(string? motivo, params object[] lineas) =>
        new { reason = motivo, results = lineas, conclusion = (string?)null };

    private static object Linea(string? observationId, decimal? valor, string? texto = null,
        string loinc = "2345-7", string codeText = "Glucosa") =>
        new { observationId, labTestId = (Guid?)null, loinc, codeText, valueNumeric = valor, valueString = texto, unit = valor is null ? null : "mg/dL" };

    [Fact]
    public async Task ElTecnico_capturaYCorrige_conLaCapturaCompleta_yElFlagSeRecalcula()
    {
        var org = await OrganizacionAsync();
        var tecnico = Cliente(org.OrgId, "lab_tech");
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-tec");

        // 600 es crítico; el técnico se equivocó: era 60.
        var capturado = await CapturarAsync(tecnico, srId, 600m);
        var obsId = capturado.Observations.Single().ObservationId;

        var resp = await tecnico.PutAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}",
            Enmienda("Error de transcripción: un cero de más", Linea(obsId, 60m)));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadFromJsonAsync<AmendResultsResult>())!.Changed.Should().Be(1);

        var hoja = await (await tecnico.GetAsync($"/api/lab/results/entry-sheet/{srId}"))
            .Content.ReadFromJsonAsync<ResultEntrySheetDto>();
        var obs = hoja!.Saved!.Observations.Single();
        obs.ValueNumeric.Should().Be(60m);
        obs.FlagCode.Should().NotBe("HH", "el flag se recalcula contra los rangos con el valor corregido");
        obs.LoincCode.Should().Be("2345-7", "la pantalla ubica el resultado en su fila por LOINC");
    }

    [Fact]
    public async Task ElMotivo_quedaEnLaAuditoria_conElAntesYElDespues()
    {
        var org = await OrganizacionAsync();
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-aud");
        var capturado = await CapturarAsync(org.Admin, srId, 150m);

        await org.Admin.PutAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}",
            Enmienda("Se repitió la corrida", Linea(capturado.Observations.Single().ObservationId, 95m)));

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entrada = await db.AppAuditLog.AsNoTracking()
            .SingleAsync(a => a.OrganizationId == org.OrgId && a.Action == "results_amended");
        entrada.Target.Should().Be($"DiagnosticReport:{capturado.DiagnosticReportId}");
        entrada.Payload.Should().Contain("Se repitió la corrida").And.Contain("150").And.Contain("95");
    }

    [Fact]
    public async Task UnAnalitoQueFaltaba_seAgregaAlInforme()
    {
        var org = await OrganizacionAsync();
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-add");
        // 150 (alto) queda preliminar; un valor normal lo autovalida el motor de reglas y lo libera.
        var capturado = await CapturarAsync(org.Admin, srId, 150m);

        var resp = await org.Admin.PutAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}",
            Enmienda("Faltó cargar la observación",
                Linea(capturado.Observations.Single().ObservationId, 150m),
                Linea(null, null, texto: "Muestra levemente hemolizada", loinc: "33882-2", codeText: "Aspecto de la muestra")));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var r = (await resp.Content.ReadFromJsonAsync<AmendResultsResult>())!;
        r.Changed.Should().Be(0, "la glucosa no cambió y no se reescribe");
        r.Added.Should().Be(1);

        var hoja = await (await org.Admin.GetAsync($"/api/lab/results/entry-sheet/{srId}"))
            .Content.ReadFromJsonAsync<ResultEntrySheetDto>();
        hoja!.Saved!.Observations.Should().Contain(o => o.ValueString == "Muestra levemente hemolizada");
    }

    [Fact]
    public async Task LoLiberado_noSeModifica()
    {
        var org = await OrganizacionAsync();
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-lib");
        var capturado = await CapturarAsync(org.Admin, srId, 150m);
        (await org.Admin.PostAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}/release", new { comment = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await org.Admin.PutAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}",
            Enmienda("tarde", Linea(capturado.Observations.Single().ObservationId, 95m)));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SinMotivo_sinCambios_oVaciandoUnResultado_seRechaza()
    {
        var org = await OrganizacionAsync();
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-val");
        var capturado = await CapturarAsync(org.Admin, srId, 150m);
        var url = $"/api/lab/results/{capturado.DiagnosticReportId}";
        var obsId = capturado.Observations.Single().ObservationId;

        (await org.Admin.PutAsJsonAsync(url, Enmienda(" ", Linea(obsId, 95m))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "el motivo es obligatorio");
        (await org.Admin.PutAsJsonAsync(url, Enmienda("nada", Linea(obsId, 150m))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "sin cambios no hay nada que guardar");
        (await org.Admin.PutAsJsonAsync(url, Enmienda("borrar", Linea(obsId, null))))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "un resultado cargado no se vacía desde la captura");
    }

    [Fact]
    public async Task ElTecnico_noLibera_yQuienNoEsDelLaboratorio_noCaptura()
    {
        var org = await OrganizacionAsync();
        var tecnico = Cliente(org.OrgId, "lab_tech");
        var medico = Cliente(org.OrgId, "practitioner");
        var srId = await GlucosaRecibidaAsync(org.Admin, "pat-perm");

        (await medico.PostAsJsonAsync("/api/lab/results", Captura(srId, 90m)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "el médico lee resultados pero no los captura");

        var capturado = await CapturarAsync(tecnico, srId, 90m);
        (await tecnico.PostAsJsonAsync($"/api/lab/results/{capturado.DiagnosticReportId}/release", new { comment = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "liberar sigue siendo del validador");
    }
}
