using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Outbox;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Locations;

/// <summary>
/// Razones sociales (FAC-9): una cadena es un solo tenant, pero sus sedes pueden ser personas
/// jurídicas distintas. Al crear una sede se elige si emite con la razón social de la principal,
/// con otra ya registrada, o con una nueva que se registra en el acto.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LegalEntitiesTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string RtnNorte = "08011999123456";

    private async Task<(HttpClient Client, Guid OrgId)> AuthedOrgAsync()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "duenio", "duenio@imed.test");

        var bootstrapClient = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "duenio"));
        var bootstrap = await (await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Red de Clínicas",
            nationalTaxId = "0801-1990-111112",
        })).Content.ReadFromJsonAsync<BootstrapProbe>();

        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: bootstrap!.OrganizationId.ToString(), realmRoles: ["org_admin"]);
        return (fixture.CreateClientWithToken(token), bootstrap.OrganizationId);
    }

    private static object NuevaRazon(string rtn = RtnNorte) => new
    {
        legalName = "Clínica del Norte S.A. de C.V.",
        taxId = rtn,
        fiscalAddress = "Col. Kennedy, Tegucigalpa",
        tradeName = "Clínica del Norte",
    };

    [Fact]
    public async Task ElAlta_creaLaRazonSocialPorDefectoConElRtnDeLaOrganizacion()
    {
        var (client, _) = await AuthedOrgAsync();

        var razones = await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities");

        var unica = razones.Should().ContainSingle().Subject;
        unica.IsDefault.Should().BeTrue();
        unica.TaxId.Should().Be("08011990111112", "el RTN del alta se guarda sin guiones");
        unica.LocationCount.Should().Be(1, "la sede principal emite con ella");
    }

    [Fact]
    public async Task UnaSedeSinElegirRazonSocial_emiteConLaDeLaPrincipal()
    {
        var (client, _) = await AuthedOrgAsync();

        var sede = await (await client.PostAsJsonAsync("/api/locations", new { code = "SUR", name = "Sucursal Sur" }))
            .Content.ReadFromJsonAsync<LocationDto>();

        var principal = (await client.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single(l => l.IsDefault);
        sede!.LegalEntity!.Id.Should().Be(principal.LegalEntity!.Id,
            "una sucursal más de la misma persona jurídica comparte RTN");
        (await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities"))
            .Should().ContainSingle("no se registró ninguna razón social nueva");
    }

    [Fact]
    public async Task UnaSedeQueEsOtraPersonaJuridica_registraSuRazonSocial()
    {
        var (client, _) = await AuthedOrgAsync();

        var resp = await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE",
            name = "Sede Norte",
            address = "Bulevar Kennedy, frente a la UNAH",
            phone = "2222-3333",
            newLegalEntity = NuevaRazon("0801-1999-123456"),
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var sede = await resp.Content.ReadFromJsonAsync<LocationDto>();

        sede!.Address.Should().Be("Bulevar Kennedy, frente a la UNAH");
        sede.LegalEntity!.TaxId.Should().Be(RtnNorte);
        sede.LegalEntity.IsDefault.Should().BeFalse();

        var razones = await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities");
        razones.Should().HaveCount(2);
        razones!.Single(r => !r.IsDefault).FiscalAddress.Should().Be("Col. Kennedy, Tegucigalpa");
    }

    [Fact]
    public async Task UnaTerceraSede_puedeEmitirConUnaRazonSocialYaRegistrada()
    {
        var (client, _) = await AuthedOrgAsync();
        var norte = await (await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE", name = "Sede Norte", newLegalEntity = NuevaRazon(),
        })).Content.ReadFromJsonAsync<LocationDto>();

        var anexo = await (await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE-2", name = "Anexo Norte", legalEntityId = norte!.LegalEntity!.Id,
        })).Content.ReadFromJsonAsync<LocationDto>();

        anexo!.LegalEntity!.Id.Should().Be(norte.LegalEntity.Id);
        (await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities"))!
            .Single(r => r.Id == norte.LegalEntity.Id).LocationCount.Should().Be(2);
    }

    [Fact]
    public async Task UnRtnMalFormado_seRechazaSinDejarNadaAMedias()
    {
        var (client, _) = await AuthedOrgAsync();

        var resp = await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE", name = "Sede Norte", newLegalEntity = NuevaRazon("0801-1999-12"),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetFromJsonAsync<List<LocationDto>>("/api/locations")).Should().ContainSingle();
        (await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities")).Should().ContainSingle();
    }

    [Fact]
    public async Task UnRtnYaRegistrado_noSeDuplicaComoOtraRazonSocial()
    {
        var (client, _) = await AuthedOrgAsync();
        await client.PostAsJsonAsync("/api/locations", new { code = "NORTE", name = "Sede Norte", newLegalEntity = NuevaRazon() });

        var resp = await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE-2", name = "Anexo Norte", newLegalEntity = NuevaRazon(),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "dos razones sociales con el mismo RTN son la misma persona jurídica cargada dos veces");
    }

    [Fact]
    public async Task ElCodigoRepetido_noRegistraLaRazonSocial()
    {
        var (client, _) = await AuthedOrgAsync();

        var resp = await client.PostAsJsonAsync("/api/locations", new
        {
            code = "PRINCIPAL", name = "Otra principal", newLegalEntity = NuevaRazon(),
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities")).Should().ContainSingle(
            "la sede no se creó, así que su razón social tampoco debe quedar huérfana");
    }

    [Fact]
    public async Task UnaSedeConCaiVigente_noCambiaDeRazonSocial()
    {
        var (client, orgId) = await AuthedOrgAsync();
        var principal = (await client.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>().HandleAsync(orgId, "HN_SAR_CAI");
            await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
                new RegisterFiscalAuthorizationCommand(
                    orgId, principal.Id, setup.DocumentTypes.First(t => t.Code == "factura").Id,
                    "CAI-DE-LA-PRINCIPAL", "001", 1, 100, new DateOnly(2027, 12, 31)));
        }

        var resp = await client.PatchAsJsonAsync($"/api/locations/{principal.Id}", new { newLegalEntity = NuevaRazon() });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "los CAI se tramitan a nombre de un RTN: la sede seguiría numerando con los de otra persona jurídica");
    }

    [Fact]
    public async Task ElRtn_seCorrigeMientrasNoHayaEmisiones()
    {
        var (client, _) = await AuthedOrgAsync();
        var razon = (await client.GetFromJsonAsync<List<LegalEntityDto>>("/api/legal-entities"))!.Single();

        var resp = await client.PutAsJsonAsync($"/api/legal-entities/{razon.Id}", new
        {
            legalName = "Red de Clínicas S. de R.L.",
            taxId = "08011990999999",
            fiscalAddress = "Tegucigalpa",
            tradeName = (string?)null,
            phone = (string?)null,
            email = (string?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var corregida = await resp.Content.ReadFromJsonAsync<LegalEntityDto>();
        corregida!.TaxId.Should().Be("08011990999999");
        corregida.TaxIdLocked.Should().BeFalse();
    }

    [Fact]
    public async Task LaSincronizacion_proyectaRazonSocialYSedeAHapi()
    {
        var (client, orgId) = await AuthedOrgAsync();
        var sede = await (await client.PostAsJsonAsync("/api/locations", new
        {
            code = "NORTE", name = "Sede Norte", address = "Bulevar Kennedy", newLegalEntity = NuevaRazon(),
        })).Content.ReadFromJsonAsync<LocationDto>();

        // Se despacha a mano en vez de esperar al procesador del outbox: la prueba es sobre lo que
        // se escribe, no sobre cuándo.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetServices<IOutboxDispatcher>()
                .Single(d => d.EventType == LocationFhirSyncEvent.OutboxEventType);
            await dispatcher.DispatchAsync(
                JsonSerializer.Serialize(new LocationFhirSyncEvent.Payload(orgId, sede!.Id, null)), default);
        }

        var location = fixture.Hapi.GetResource("Location", OrganizationLocation.FhirIdFor(sede!.Id));
        location.Should().NotBeNull();
        ((string?)location!["managingOrganization"]!["reference"])
            .Should().Be($"Organization/{LegalEntity.FhirIdFor(sede.LegalEntity!.Id)}");
        ((string?)location["address"]!["text"]).Should().Be("Bulevar Kennedy");

        var organization = fixture.Hapi.GetResource("Organization", LegalEntity.FhirIdFor(sede.LegalEntity.Id));
        organization.Should().NotBeNull();
        ((string?)organization!["identifier"]![0]!["value"]).Should().Be(RtnNorte);
        ((string?)organization["partOf"]!["reference"]).Should().StartWith("Organization/",
            "la razón social cuelga del tenant, en su misma partición");

        using var check = fixture.Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.OrganizationLocations.AsNoTracking().SingleAsync(l => l.Id == sede.Id))
            .LocationFhirId.Should().Be(OrganizationLocation.FhirIdFor(sede.Id));
    }

    private sealed record BootstrapProbe(Guid OrganizationId);
}
