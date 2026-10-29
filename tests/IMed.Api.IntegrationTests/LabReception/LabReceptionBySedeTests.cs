using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Locations;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Las colas de la recepción del laboratorio son por sede (FAC-9). Observación de staging: una
/// sucursal nueva veía las órdenes de la otra y, al trabajarlas, el servidor las rechazaba por
/// sede. Cada lista se reparte por la sede donde la orden ingresó; "por admitir", que todavía no
/// tiene ingreso, por la sede de la consulta que la pidió.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabReceptionBySedeTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Un mismo usuario, asignado a las dos sedes, parado en una o en otra.</summary>
    private sealed record Red(HttpClient EnPrincipal, HttpClient EnNorte, LocationDto Principal, LocationDto Norte);

    private async Task<Red> RedConDosSedesAsync()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "recepcion", "recepcion@imed.test");
        var bootstrap = await (await fixture.CreateClientWithToken(
                fixture.Keycloak.IssueUserToken(userId, preferredUsername: "recepcion"))
            .PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Red de Laboratorios", nationalTaxId = "08011990111113" }))
            .Content.ReadFromJsonAsync<BootstrapProbe>();
        var orgId = bootstrap!.OrganizationId;
        await fixture.ProvisionLabCatalogAsync(orgId);

        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: ["org_admin", "lab_tech"]);
        var enPrincipal = fixture.CreateClientWithToken(token);
        var principal = (await enPrincipal.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single();

        var norte = (await (await enPrincipal.PostAsJsonAsync("/api/locations", new { code = "NORTE", name = "Sucursal Norte" }))
            .Content.ReadFromJsonAsync<LocationDto>())!;
        (await enPrincipal.PostAsJsonAsync($"/api/locations/{norte.Id}/users/{userId}", new { makePrimary = false }))
            .IsSuccessStatusCode.Should().BeTrue();

        var enNorte = fixture.CreateClientWithToken(token);
        enNorte.DefaultRequestHeaders.Add("X-IMed-Location", norte.Id.ToString());
        return new Red(enPrincipal, enNorte, principal, norte);
    }

    private static async Task<List<T>> ListaAsync<T>(HttpClient client, string url)
    {
        var resp = await client.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<List<T>>())!;
    }

    [Fact]
    public async Task PendientesYColaDeToma_soloDeLaSedeDondeIngreso()
    {
        var red = await RedConDosSedesAsync();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest("pat-sede-1", "2345-7", "Glucosa", encounter: enc, requisition: enc);

        (await red.EnNorte.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListaAsync<OrdenProbe>(red.EnNorte, "/api/lab/reception/pending"))
            .Should().Contain(o => o.ServiceRequestId == srId);
        (await ListaAsync<OrdenProbe>(red.EnPrincipal, "/api/lab/reception/pending"))
            .Should().NotContain(o => o.ServiceRequestId == srId, "ingresó en la Sucursal Norte");

        (await ListaAsync<OrdenProbe>(red.EnNorte, "/api/lab/reception/collection-queue"))
            .Should().Contain(o => o.ServiceRequestId == srId, "la glucosa se toma en el laboratorio");
        (await ListaAsync<OrdenProbe>(red.EnPrincipal, "/api/lab/reception/collection-queue"))
            .Should().NotContain(o => o.ServiceRequestId == srId);
    }

    [Fact]
    public async Task RecepcionTecnica_soloLosTubosDeLaSede()
    {
        var red = await RedConDosSedesAsync();
        var enc = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-sede-2", "CMP", "Perfil metabolico completo", encounter: enc, requisition: enc);

        (await red.EnNorte.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await red.EnNorte.PostAsJsonAsync("/api/lab/reception/collection-plan", new { requisition = enc }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var labeled = await (await red.EnNorte.PostAsJsonAsync($"/api/lab/reception/collection-plan/{enc}/labels", new { }))
            .Content.ReadFromJsonAsync<PlanProbe>();

        // La sangre llega al bench recién tomada: identificación positiva, inicio y toma.
        var containerUrl = $"/api/lab/reception/collection-plan/container/{labeled!.Containers[0].Id}";
        (await red.EnNorte.PostAsJsonAsync($"{containerUrl}/verify-identity", new
        {
            verifiedName = true,
            verifiedBirthDate = true,
            stage = "Collection",
            patientName = "Paciente Norte",
            patientBirthDate = "1985-04-02",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await red.EnNorte.PostAsJsonAsync($"{containerUrl}/advance", new { action = "start", detail = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await red.EnNorte.PostAsJsonAsync($"{containerUrl}/advance", new { action = "collect", detail = (string?)null }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ListaAsync<RequisicionProbe>(red.EnNorte, "/api/lab/reception/technical-reception"))
            .Should().Contain(o => o.Requisition == enc);
        (await ListaAsync<RequisicionProbe>(red.EnPrincipal, "/api/lab/reception/technical-reception"))
            .Should().NotContain(o => o.Requisition == enc, "la muestra se tomó en la Sucursal Norte");
    }

    [Fact]
    public async Task PorAdmitir_porLaSedeDeLaConsulta_conOpcionDeVerTodas()
    {
        var red = await RedConDosSedesAsync();

        var encNorte = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-sede-3", "2345-7", "Glucosa", encounter: encNorte, requisition: encNorte);
        fixture.Hapi.RegisterEncounterLocation(encNorte, $"Location/{OrganizationLocation.FhirIdFor(red.Norte.Id)}");

        // Una consulta sin sede es anterior a FAC-9: cuenta como de la principal.
        var encViejo = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-sede-4", "2345-7", "Glucosa", encounter: encViejo, requisition: encViejo);

        var enNorte = await ListaAsync<RequisicionProbe>(red.EnNorte, "/api/lab/reception/awaiting-admission");
        enNorte.Should().Contain(o => o.Requisition == encNorte);
        enNorte.Should().NotContain(o => o.Requisition == encViejo);

        var enPrincipal = await ListaAsync<RequisicionProbe>(red.EnPrincipal, "/api/lab/reception/awaiting-admission");
        enPrincipal.Should().Contain(o => o.Requisition == encViejo);
        enPrincipal.Should().NotContain(o => o.Requisition == encNorte, "la consulta fue en la Sucursal Norte");

        // El paciente puede presentarse en el laboratorio de otra sucursal.
        (await ListaAsync<RequisicionProbe>(red.EnPrincipal, "/api/lab/reception/awaiting-admission?allLocations=true"))
            .Should().Contain(o => o.Requisition == encNorte)
            .And.Contain(o => o.Requisition == encViejo);
    }

    private sealed record PlanProbe(List<ContainerProbe> Containers);
    private sealed record ContainerProbe(Guid Id);
    private sealed record OrdenProbe(string ServiceRequestId);
    private sealed record RequisicionProbe(string? Requisition);
    private sealed record BootstrapProbe(Guid OrganizationId);
}
