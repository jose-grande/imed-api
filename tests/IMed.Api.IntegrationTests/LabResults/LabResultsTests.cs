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
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(),
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

    /// <summary>
    /// Idempotencia de la captura. Antes, cada POST creaba un informe nuevo sin preguntar si
    /// la orden ya tenía uno, así que un reintento —o un técnico que salió de la pantalla,
    /// volvió, la encontró en blanco y redigitó— dejaba dos informes con observaciones
    /// duplicadas para el mismo paciente y la misma orden, en silencio.
    /// </summary>
    [Fact]
    public async Task EnterResults_dosVeces_noDuplicaElInforme_y409()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-dup");

        var first = await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 85m));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 91m));
        second.StatusCode.Should().Be(HttpStatusCode.Conflict, "la orden ya tiene resultados");

        var summaries = await (await client.GetAsync("/api/lab/results/by-patient/pat-dup"))
            .Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        summaries!.Should().ContainSingle("el segundo guardado no debe crear un informe nuevo");
    }

    /// <summary>
    /// La hoja de captura devuelve lo ya guardado, que es lo que permite reabrir la pantalla
    /// en modo revisión/corrección. Sin esto, el estado de "guardado" solo vivía en memoria
    /// del componente y salir de la pantalla lo perdía para siempre.
    /// </summary>
    [Fact]
    public async Task EntrySheet_traeLosResultadosYaGuardados()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-sheet");

        var before = await (await client.GetAsync($"/api/lab/results/entry-sheet/{srId}"))
            .Content.ReadFromJsonAsync<ResultEntrySheetDto>();
        before!.Saved.Should().BeNull("todavía no se resultó");

        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();

        var after = await (await client.GetAsync($"/api/lab/results/entry-sheet/{srId}"))
            .Content.ReadFromJsonAsync<ResultEntrySheetDto>();
        after!.Saved.Should().NotBeNull();
        after.Saved!.ReportId.Should().Be(entered!.DiagnosticReportId);
        after.Saved.Origin.Should().Be("InHouse");
        after.Saved.Observations.Should().ContainSingle();
        after.Saved.Observations[0].ValueNumeric.Should().Be(150m);
        after.Saved.Observations[0].FlagCode.Should().Be("H");
    }

    /// <summary>
    /// El desenlace que le faltaba a Validación. Sus únicas opciones eran liberar o irse, e irse
    /// no registraba nada: el siguiente validador volvía a encontrar el mismo pendiente sin
    /// ninguna nota. Devolver saca el informe de la cola y deja el motivo por escrito.
    /// </summary>
    [Fact]
    public async Task RejectReport_loSacaDeLaCola_yCorregirLoDevuelve()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-reject");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();
        var reportId = entered!.DiagnosticReportId;

        var pendingBefore = await (await client.GetAsync("/api/lab/results/pending"))
            .Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        pendingBefore!.Should().Contain(r => r.ReportId == reportId);

        var rejected = await client.PostAsJsonAsync(
            $"/api/lab/results/{reportId}/reject", new { reasonCode = "RERUN_REQUIRED", note = "Repetir con dilución." });
        rejected.StatusCode.Should().Be(HttpStatusCode.OK);

        var pendingAfter = await (await client.GetAsync("/api/lab/results/pending"))
            .Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        pendingAfter!.Should().NotContain(r => r.ReportId == reportId, "está en manos del técnico");

        // El técnico corrige: eso responde la devolución y el informe vuelve a la cola.
        var obsId = entered.Observations[0].ObservationId;
        var corrected = await client.PostAsJsonAsync(
            $"/api/lab/results/observations/{obsId}/correct",
            new { valueNumeric = 95m, valueString = (string?)null, unit = (string?)null, reason = "Repetición" });
        corrected.StatusCode.Should().Be(HttpStatusCode.OK);

        var pendingBack = await (await client.GetAsync("/api/lab/results/pending"))
            .Content.ReadFromJsonAsync<LabReportSummaryDto[]>();
        pendingBack!.Should().Contain(r => r.ReportId == reportId, "la corrección cierra la devolución");
    }

    /// <summary>Devolver dos veces el mismo informe no tiene sentido: ya está con el técnico.</summary>
    [Fact]
    public async Task RejectReport_dosVeces_retorna409()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-reject2");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();

        var body = new { reasonCode = "RERUN_REQUIRED", note = (string?)null };
        (await client.PostAsJsonAsync($"/api/lab/results/{entered!.DiagnosticReportId}/reject", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/lab/results/{entered.DiagnosticReportId}/reject", body))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>Un motivo fuera del catálogo se rechaza: por eso es catálogo y no texto libre.</summary>
    [Fact]
    public async Task RejectReport_motivoDesconocido_retorna409()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-reject3");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();

        var resp = await client.PostAsJsonAsync(
            $"/api/lab/results/{entered!.DiagnosticReportId}/reject",
            new { reasonCode = "NO_EXISTE", note = (string?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// El detalle tiene que traer el "por qué está acá": las reglas que dispararon. Se venían
    /// guardando desde LIS-5 sin que nada las leyera, y son lo que le da criterio al validador —
    /// la auto-validación ya libera sola lo normal, así que lo que llega es lo que el motor no
    /// quiso firmar.
    ///
    /// <para>El nombre del paciente se resuelve contra HAPI y acá degrada a null a propósito:
    /// estos tests usan ids de paciente sueltos que nunca se crean como recurso FHIR. Lo que se
    /// verifica es que la resolución no rompa el detalle.</para>
    /// </summary>
    [Fact]
    public async Task ResultDetail_traeLasReglasQueDispararon()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-detail");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 600m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();
        entered!.HasCritical.Should().BeTrue("600 es crítico y por eso llega a validación");

        var detail = await (await client.GetAsync($"/api/lab/results/{entered.DiagnosticReportId}"))
            .Content.ReadFromJsonAsync<LabReportDetailDto>();

        detail.Should().NotBeNull();
        detail!.RuleHits.Should().NotBeNull();
        detail.RuleHits!.Should().NotBeEmpty("alguna regla tuvo que disparar para retenerlo");
        detail.RuleHits.Should().OnlyContain(h => !string.IsNullOrWhiteSpace(h.RuleName),
            "el validador necesita el nombre de la regla, no un GUID");
    }

    /// <summary>
    /// El informe imprimible: lo que se lleva el paciente. Se verifica que salga un PDF real y
    /// que el nombre del archivo distinga preliminar de liberado — un preliminar archivado como
    /// definitivo es el error que este documento tiene que hacer imposible.
    /// </summary>
    [Fact]
    public async Task ReportPdf_marcaPreliminarAntesDeLiberar_yDefinitivoDespues()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-pdf");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 600m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();
        var reportId = entered!.DiagnosticReportId;

        var preliminary = await client.GetAsync($"/api/lab/results/{reportId}/pdf");
        preliminary.StatusCode.Should().Be(HttpStatusCode.OK);
        preliminary.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var preliminaryBytes = await preliminary.Content.ReadAsByteArrayAsync();
        preliminaryBytes.Should().NotBeEmpty();
        System.Text.Encoding.ASCII.GetString(preliminaryBytes, 0, 5).Should().Be("%PDF-");
        preliminary.Content.Headers.ContentDisposition!.FileNameStar!.Should().Contain("PRELIMINAR");

        (await client.PostAsJsonAsync($"/api/lab/results/{reportId}/release", new { comment = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var final = await client.GetAsync($"/api/lab/results/{reportId}/pdf");
        final.StatusCode.Should().Be(HttpStatusCode.OK);
        (await final.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        final.Content.Headers.ContentDisposition!.FileNameStar!.Should().NotContain("PRELIMINAR");
    }

    /// <summary>Un informe inexistente no puede devolver un PDF vacío disfrazado de éxito.</summary>
    [Fact]
    public async Task ReportPdf_informeInexistente_retorna404()
    {
        var client = await AuthedClient();
        (await client.GetAsync("/api/lab/results/no-existe/pdf"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>El comentario del validador va al informe, no a una tabla interna que nadie lee.</summary>
    [Fact]
    public async Task ReleaseReport_conComentario_loGuardaEnElInforme()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-comment");
        var entered = await (await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 150m)))
            .Content.ReadFromJsonAsync<EnterResultsResult>();

        var released = await client.PostAsJsonAsync(
            $"/api/lab/results/{entered!.DiagnosticReportId}/release",
            new { comment = "Confirmado con segunda corrida." });
        released.StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await (await client.GetAsync($"/api/lab/results/{entered.DiagnosticReportId}"))
            .Content.ReadFromJsonAsync<LabReportDetailDto>();
        detail!.Status.Should().Be("final");
        detail.Conclusion.Should().Be("Confirmado con segunda corrida.");
    }

    /// <summary>
    /// La worklist tiene que distinguir una orden ya resultada, o sigue ofreciendo "Ingresar
    /// resultados" sobre algo que ya tiene informe.
    /// </summary>
    [Fact]
    public async Task Worklist_marcaLasOrdenesYaResultadas()
    {
        var client = await AuthedClient();
        var srId = await ReceiveGlucoseAsync(client, "pat-wl");

        var before = await (await client.GetAsync("/api/lab/worklist?status=Received"))
            .Content.ReadFromJsonAsync<WorklistItem[]>();
        before!.Single(x => x.ServiceRequestId == srId).HasResults.Should().BeFalse();

        await client.PostAsJsonAsync("/api/lab/results", ResultsBody(srId, 85m));

        var after = await (await client.GetAsync("/api/lab/worklist?status=Received"))
            .Content.ReadFromJsonAsync<WorklistItem[]>();
        after!.Single(x => x.ServiceRequestId == srId).HasResults.Should().BeTrue();
    }
}
