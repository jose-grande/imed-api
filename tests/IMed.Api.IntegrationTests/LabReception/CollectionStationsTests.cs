using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Ciclo de la muestra por estaciones (R6): plan → etiqueta → identificación → toma → QC.
///
/// <para>Cubre en particular la <b>convergencia</b> con el flujo operativo: al aceptar el QC se
/// materializa el <c>Specimen</c> FHIR y se abre el <c>order_intake</c> del que comen Resultados,
/// dashboard y TAT. Y la <b>cadena de custodia</b>: el camino nuevo debe escribir <c>Provenance</c>
/// igual que el legacy — la asimetría entre ambos era el ítem A8 del roadmap.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CollectionStationsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task QcAceptado_materializaLaMuestraYFirmaLaCadenaDeCustodia()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "tecnico", "tecnico@imed.test");

        // La organización tiene que existir en imed_app, no solo en el claim del token: el
        // perfil de profesional se ancla a ella. Por eso se arranca por el bootstrap real.
        var bootstrapClient = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "tecnico"));
        var bootstrap = await (await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Laboratorio Estaciones",
            nationalTaxId = "0801-1990-54321",
        })).Content.ReadFromJsonAsync<BootstrapProbe>();
        bootstrap.Should().NotBeNull();
        var orgId = bootstrap!.OrganizationId;

        await fixture.ProvisionLabCatalogAsync(orgId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: ["lab_tech", "org_admin"]);
        var client = fixture.CreateClientWithToken(token);

        // El Provenance se le atribuye al Practitioner detrás del usuario: sin perfil no hay
        // a quién imputarle el acto, y el camino queda sin firma (best-effort, no rompe).
        var profile = await client.PutAsJsonAsync("/api/me/practitioner-profile", new
        {
            family = "Flores",
            given = new[] { "Ana" },
            prefix = (string?)null,
            licenseNumber = "LIC-STATIONS-1",
            email = (string?)null,
            phone = (string?)null,
            gender = (string?)null,
            specialtyCode = (string?)null,
            specialtyDisplay = (string?)null,
        });
        profile.StatusCode.Should().Be(HttpStatusCode.OK);

        var enc = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-stations-1", "CMP", "Perfil metabolico completo", encounter: enc, requisition: enc);
        (await client.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Estación 1 — plan de colección: la orden se traduce a contenedores físicos.
        var planResp = await client.PostAsJsonAsync("/api/lab/reception/collection-plan", new { requisition = enc });
        planResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Estación 2 — etiqueta: aparece el accession, que es lo que vuelve trazable al tubo.
        var labeled = await (await client.PostAsJsonAsync($"/api/lab/reception/collection-plan/{enc}/labels", new { }))
            .Content.ReadFromJsonAsync<CollectionPlanDto>();
        labeled.Should().NotBeNull();
        // El panel consolida en más de un tubo según aditivo; se recorre uno, que es la unidad
        // física que atraviesa las estaciones.
        labeled!.Containers.Should().NotBeEmpty();
        var container = labeled.Containers[0];
        container.AccessionNumber.Should().NotBeNullOrWhiteSpace();

        // El accession lleva el segmento de sede (ADR-008). No es decorativo: con correlativos
        // independientes por sede, sin él dos sucursales emitirían el mismo número el mismo día.
        container.AccessionNumber!.Split('-').Should().HaveCount(4, "ORG-SEDE-FECHA-SECUENCIA");
        container.AccessionNumber.Split('-')[1].Should().Be("PRIN", "es la sede principal, PRINCIPAL truncada a 4");

        var containerUrl = $"/api/lab/reception/collection-plan/container/{container.Id}";

        // Estación 3 — identificación positiva antes de pinchar al paciente.
        (await client.PostAsJsonAsync($"{containerUrl}/verify-identity", new
        {
            verifiedName = true,
            verifiedBirthDate = true,
            stage = "Collection",
            patientName = "Paciente Estaciones",
            patientBirthDate = "1985-04-02",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync($"{containerUrl}/advance", new { action = "start", detail = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"{containerUrl}/advance", new { action = "collect", detail = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Estación 4 — recepción técnica: el QC conforme mete la muestra al laboratorio.
        (await client.PostAsJsonAsync($"{containerUrl}/qc", new
        {
            accepted = true,
            rejectionReasonCode = (string?)null,
            note = (string?)null,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        // La convergencia abrió el intake: la orden ya aparece para resultar.
        var worklist = await (await client.GetAsync("/api/lab/worklist"))
            .Content.ReadFromJsonAsync<List<WorklistItemProbe>>();
        worklist.Should().NotBeNull();
        worklist!.Should().NotBeEmpty("el QC conforme debe materializar la orden en el flujo operativo");

        // A8: la cadena de custodia del camino nuevo, que antes solo existía en el legacy.
        var provenance = fixture.Hapi.Provenances
            .Should().ContainSingle(p => p.TargetReference.StartsWith("Specimen/", StringComparison.Ordinal)).Subject;
        provenance.ActivityCode.Should().Be("CREATE");
        provenance.AgentWhoReference.Should().StartWith("Practitioner/");
    }

    /// <summary>Proyección mínima de la worklist: solo se afirma que la orden llegó.</summary>
    private sealed record WorklistItemProbe(string ServiceRequestId, string Status);

    /// <summary>Lo único que se necesita del bootstrap: el id de la organización creada.</summary>
    private sealed record BootstrapProbe(Guid OrganizationId);
}
