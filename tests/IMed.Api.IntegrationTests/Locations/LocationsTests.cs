using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Locations;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Locations;

/// <summary>
/// Eje de sede (ADR-008): la sede es un <c>Location</c> dentro de una organización, no una
/// organización hija. Cubre el aprovisionamiento automático, la creación de sedes adicionales,
/// el traspaso de la marca de principal y el alcance del usuario.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LocationsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Crea una organización real y devuelve cliente autenticado sobre ella.</summary>
    private async Task<(HttpClient Client, Guid OrgId, Guid UserId)> AuthedOrgAsync(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "duenio", "duenio@imed.test");

        var bootstrapClient = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "duenio"));
        var bootstrap = await (await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Red de Clínicas",
            nationalTaxId = "0801-1990-11111",
        })).Content.ReadFromJsonAsync<BootstrapProbe>();
        bootstrap.Should().NotBeNull();

        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: bootstrap!.OrganizationId.ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return (fixture.CreateClientWithToken(token), bootstrap.OrganizationId, userId);
    }

    [Fact]
    public async Task Bootstrap_dejaLaOrganizacionConSedePrincipal()
    {
        var (client, _, _) = await AuthedOrgAsync();

        var locations = await (await client.GetAsync("/api/locations")).Content.ReadFromJsonAsync<List<LocationDto>>();

        locations.Should().NotBeNull();
        var principal = locations!.Should().ContainSingle(
            "una organización recién creada opera en un solo lugar").Subject;
        principal.IsDefault.Should().BeTrue();
        principal.Active.Should().BeTrue();
        principal.Code.Should().Be("PRINCIPAL");
    }

    [Fact]
    public async Task SegundaSede_seCreaYPuedeTomarLaMarcaDePrincipal()
    {
        var (client, _, _) = await AuthedOrgAsync();

        var creada = await (await client.PostAsJsonAsync("/api/locations", new
        {
            code = "SUCURSAL-NORTE",
            name = "Sucursal Norte",
        })).Content.ReadFromJsonAsync<LocationDto>();

        creada.Should().NotBeNull();
        creada!.IsDefault.Should().BeFalse("la primera sede ya es la principal");

        // Traspasar la marca: el índice único filtrado no admite dos principales a la vez, así
        // que esto también verifica que se suelte la anterior antes de marcar la nueva.
        var promovida = await (await client.PatchAsJsonAsync($"/api/locations/{creada.Id}", new
        {
            name = (string?)null,
            active = (bool?)null,
            makeDefault = true,
        })).Content.ReadFromJsonAsync<LocationDto>();
        promovida!.IsDefault.Should().BeTrue();

        var todas = await (await client.GetAsync("/api/locations")).Content.ReadFromJsonAsync<List<LocationDto>>();
        todas!.Should().HaveCount(2);
        todas.Where(l => l.IsDefault).Should().ContainSingle("solo puede haber una sede principal");
        todas.Single(l => l.IsDefault).Code.Should().Be("SUCURSAL-NORTE");
    }

    [Fact]
    public async Task CodigoRepetido_enLaMismaOrganizacion_da409()
    {
        var (client, _, _) = await AuthedOrgAsync();

        (await client.PostAsJsonAsync("/api/locations", new { code = "CENTRO", name = "Centro" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/locations", new { code = "centro", name = "Centro duplicado" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "el código se normaliza en mayúsculas");
    }

    [Fact]
    public async Task LaSedePrincipal_noSePuedeDesactivar()
    {
        var (client, _, _) = await AuthedOrgAsync();
        var principal = (await (await client.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();

        var resp = await client.PatchAsJsonAsync($"/api/locations/{principal.Id}", new
        {
            name = (string?)null,
            active = false,
            makeDefault = (bool?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "desactivar la única sede dejaría a la organización sin dónde operar");
    }

    // FAC-9: "sin asignaciones ve todas" dejó de ser cierto. Un usuario sin sede queda asignado a
    // la principal; lo cubre UserLocationsTests.UnUsuarioSinSede_quedaAsignadoALaPrincipal_yNoVeLasDemas.

    [Fact]
    public async Task ConAsignacion_elUsuarioSoloVeSusSedes()
    {
        var (client, orgId, _) = await AuthedOrgAsync();
        var sur = await (await client.PostAsJsonAsync("/api/locations", new { code = "SUR", name = "Sucursal Sur" }))
            .Content.ReadFromJsonAsync<LocationDto>();

        // Un técnico distinto del dueño, asignado solo a la sucursal Sur.
        var tecnicoId = Guid.NewGuid();
        (await client.PostAsJsonAsync($"/api/locations/{sur!.Id}/users/{tecnicoId}", new { makePrimary = true }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        fixture.Keycloak.RegisterUser(tecnicoId, "tecnico", "tecnico@imed.test");
        var tecnicoToken = fixture.Keycloak.IssueUserToken(
            tecnicoId, organizationId: orgId.ToString(), realmRoles: ["lab_tech"]);
        var tecnicoClient = fixture.CreateClientWithToken(tecnicoToken);

        var mias = await (await tecnicoClient.GetAsync("/api/locations/mine"))
            .Content.ReadFromJsonAsync<List<MyLocationDto>>();

        mias.Should().ContainSingle("tiene una sola sede asignada")
            .Which.Code.Should().Be("SUR");
    }

    [Fact]
    public async Task LaSedeDeclaradaEnElHeader_mandaSobreLaInferida()
    {
        var (client, _, userId) = await AuthedOrgAsync("lab_tech", "org_admin");
        var sur = await (await client.PostAsJsonAsync("/api/locations", new { code = "SUR", name = "Sucursal Sur" }))
            .Content.ReadFromJsonAsync<LocationDto>();
        // Desde FAC-9 el usuario solo declara sedes donde está asignado: el fundador nace en la
        // principal y hay que habilitarlo en la Sur.
        await client.PostAsJsonAsync($"/api/locations/{sur!.Id}/users/{userId}", new { makePrimary = false });

        var enc = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-sede-1", "CMP", "Perfil metabolico completo", encounter: enc, requisition: enc);

        // El usuario declara que está parado en la Sucursal Sur. Es lo que hace que el selector
        // de sede signifique algo: sin esto el backend inferiría siempre la principal.
        client.DefaultRequestHeaders.Add("X-IMed-Location", sur.Id.ToString());

        await client.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc });
        await client.PostAsJsonAsync("/api/lab/reception/collection-plan", new { requisition = enc });
        var labeled = await (await client.PostAsJsonAsync($"/api/lab/reception/collection-plan/{enc}/labels", new { }))
            .Content.ReadFromJsonAsync<CollectionPlanProbe>();

        labeled.Should().NotBeNull();
        var accession = labeled!.Containers[0].AccessionNumber;
        accession.Should().NotBeNullOrWhiteSpace();
        accession!.Split('-')[1].Should().Be("SUR",
            "el accession lleva la sede declarada, no la principal de la organización");
    }

    [Fact]
    public async Task UnaSedeAjena_declaradaEnElHeader_noSeAcepta()
    {
        // Organización A crea su sede; el usuario de la organización B intenta usarla.
        var (clientA, _, _) = await AuthedOrgAsync();
        var ajena = await (await clientA.PostAsJsonAsync("/api/locations", new { code = "AJENA", name = "Sede ajena" }))
            .Content.ReadFromJsonAsync<LocationDto>();

        var (clientB, _, _) = await AuthedOrgAsync("lab_tech", "org_admin");
        clientB.DefaultRequestHeaders.Add("X-IMed-Location", ajena!.Id.ToString());

        var enc = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.SeedServiceRequest("pat-sede-2", "CMP", "Perfil metabolico completo", encounter: enc, requisition: enc);

        var resp = await clientB.PostAsJsonAsync("/api/lab/reception/check-in", new { requisition = enc });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "un header no puede habilitar a operar en la sede de otra organización");
    }

    // ---- Código de establecimiento fiscal (FSF bloque 0) --------------------------------------

    [Fact]
    public async Task LaSedeReciénCreada_noTraeCodigoDeEstablecimiento()
    {
        var (client, _, _) = await AuthedOrgAsync();

        var locations = await (await client.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>();

        // No se inventa: el código lo asigna el SAR, y poner uno por defecto haría salir facturas
        // con un establecimiento que la autoridad no reconoce.
        locations!.Single().FiscalEstablishmentCode.Should().BeNull();
    }

    [Fact]
    public async Task ElSupervisorDeFacturacion_fijaElCodigoSinSerAdministradorDeSedes()
    {
        // El código sale del mismo trámite del SAR que el CAI. Si hiciera falta `org.locations.manage`,
        // quien registra el CAI tendría que ir a buscar a un administrador para escribir tres dígitos.
        var (client, _, _) = await AuthedOrgAsync("billing_supervisor");
        var sede = (await (await client.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();

        var resp = await client.PutAsJsonAsync(
            $"/api/locations/{sede.Id}/fiscal-establishment", new { code = "001" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var actualizada = await resp.Content.ReadFromJsonAsync<LocationDto>();
        actualizada!.FiscalEstablishmentCode.Should().Be("001");
    }

    [Fact]
    public async Task ElCajero_noFijaElCodigoDeEstablecimiento()
    {
        var (client, _, _) = await AuthedOrgAsync("cashier");
        var (admin, _, _) = await AuthedOrgAsync();
        var sede = (await (await admin.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();

        var resp = await client.PutAsJsonAsync(
            $"/api/locations/{sede.Id}/fiscal-establishment", new { code = "001" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "cambiar el establecimiento cambia el número de toda factura que salga de esa sede");
    }

    [Theory]
    [InlineData("1")]
    [InlineData("12")]
    [InlineData("1234")]
    [InlineData("00A")]
    [InlineData("  1")]
    public async Task UnCodigoMalFormado_seRechazaAlGuardarlo(string malo)
    {
        // Un código mal formado no se descubre al guardarlo: se descubre cuando el primer documento
        // sale con un número que la autoridad no reconoce. Por eso se valida acá y no en la pantalla.
        var (client, _, _) = await AuthedOrgAsync();
        var sede = (await (await client.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();

        var resp = await client.PutAsJsonAsync(
            $"/api/locations/{sede.Id}/fiscal-establishment", new { code = malo });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ElCodigoSePuedeBorrar_cuandoLaSedeDejaDeFacturar()
    {
        var (client, _, _) = await AuthedOrgAsync();
        var sede = (await (await client.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();
        await client.PutAsJsonAsync($"/api/locations/{sede.Id}/fiscal-establishment", new { code = "001" });

        var resp = await client.PutAsJsonAsync(
            $"/api/locations/{sede.Id}/fiscal-establishment", new { code = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<LocationDto>())!.FiscalEstablishmentCode.Should().BeNull();
    }

    [Fact]
    public async Task ElCodigoNoSeToma_deLaSedeDeOtraOrganizacion()
    {
        var (clientA, _, _) = await AuthedOrgAsync();
        var (clientB, _, _) = await AuthedOrgAsync();
        var ajena = (await (await clientA.GetAsync("/api/locations"))
            .Content.ReadFromJsonAsync<List<LocationDto>>())!.Single();

        var resp = await clientB.PutAsJsonAsync(
            $"/api/locations/{ajena.Id}/fiscal-establishment", new { code = "001" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "el id correcto con la organización equivocada no alcanza");
    }

    private sealed record BootstrapProbe(Guid OrganizationId);

    private sealed record CollectionPlanProbe(IReadOnlyList<ContainerProbe> Containers);

    private sealed record ContainerProbe(Guid Id, string? AccessionNumber);
}
