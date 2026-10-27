using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Outbox;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Locations;

/// <summary>
/// Todo está asociado a una sede (FAC-9): cada usuario tiene al menos una y una principal, cada
/// cita y cada encuentro nuevo dicen en qué sede ocurren, y el médico que atiende está asignado a
/// ella. La sede decide con qué razón social se factura lo atendido.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class UserLocationsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Org(HttpClient Admin, Guid OrgId, Guid FounderId, LocationDto Principal);

    private async Task<Org> BootstrapAsync()
    {
        await Task.Delay(800);
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "duenio", "duenio@imed.test");
        var bootstrap = await (await fixture.CreateClientWithToken(
                fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "duenio"))
            .PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Red de Clínicas", nationalTaxId = "08011990111112" }))
            .Content.ReadFromJsonAsync<BootstrapProbe>();

        var admin = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            founderId, organizationId: bootstrap!.OrganizationId.ToString(),
            realmRoles: ["org_admin", "practitioner"]));
        var principal = (await admin.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single();
        return new Org(admin, bootstrap.OrganizationId, founderId, principal);
    }

    private async Task<LocationDto> CrearSedeAsync(Org org, string code, string name) =>
        (await (await org.Admin.PostAsJsonAsync("/api/locations", new { code, name }))
            .Content.ReadFromJsonAsync<LocationDto>())!;

    private HttpClient ClienteDe(Guid orgId, Guid userId, params string[] roles)
    {
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: roles));
    }

    private async Task<List<UserLocationAssignment>> AsignacionesAsync(Guid orgId, Guid userId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.UserLocations.AsNoTracking()
            .Where(x => x.OrganizationId == orgId && x.UserId == userId)
            .ToListAsync();
    }

    // ── Usuarios ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ElFundador_quedaEnLaSedePrincipal()
    {
        var org = await BootstrapAsync();

        var asignaciones = await AsignacionesAsync(org.OrgId, org.FounderId);

        asignaciones.Should().ContainSingle().Which.Should().Match<UserLocationAssignment>(
            a => a.LocationId == org.Principal.Id && a.IsPrimary);
    }

    [Fact]
    public async Task UnUsuarioSinSede_quedaAsignadoALaPrincipal_yNoVeLasDemas()
    {
        var org = await BootstrapAsync();
        await CrearSedeAsync(org, "SUR", "Sucursal Sur");

        var tecnicoId = Guid.NewGuid();
        var mias = await ClienteDe(org.OrgId, tecnicoId, "lab_tech")
            .GetFromJsonAsync<List<MyLocationDto>>("/api/locations/mine");

        mias.Should().ContainSingle(
            "la sede decide con qué razón social se factura lo que atiende: 'todas' ya no es neutral")
            .Which.Id.Should().Be(org.Principal.Id);
        (await AsignacionesAsync(org.OrgId, tecnicoId)).Should().ContainSingle(
            "la asignación queda guardada, no se recalcula en cada request");
    }

    [Fact]
    public async Task LaUnicaSedeDeUnUsuario_noSePuedeQuitar()
    {
        var org = await BootstrapAsync();

        var resp = await org.Admin.DeleteAsync($"/api/locations/{org.Principal.Id}/users/{org.FounderId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await AsignacionesAsync(org.OrgId, org.FounderId)).Should().ContainSingle();
    }

    [Fact]
    public async Task QuitarLaSedePrincipal_promueveOtraDeSusSedes()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");
        await org.Admin.PostAsJsonAsync($"/api/locations/{sur.Id}/users/{org.FounderId}", new { makePrimary = false });

        (await org.Admin.DeleteAsync($"/api/locations/{org.Principal.Id}/users/{org.FounderId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await AsignacionesAsync(org.OrgId, org.FounderId)).Should().ContainSingle()
            .Which.Should().Match<UserLocationAssignment>(a => a.LocationId == sur.Id && a.IsPrimary);
    }

    [Fact]
    public async Task FijarLasSedes_reemplazaElConjuntoYLaPrincipal()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");
        var norte = await CrearSedeAsync(org, "NORTE", "Sucursal Norte");

        var resp = await org.Admin.PutAsJsonAsync($"/api/locations/users/{org.FounderId}", new
        {
            locationIds = new[] { sur.Id, norte.Id },
            primaryLocationId = norte.Id,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var asignaciones = await AsignacionesAsync(org.OrgId, org.FounderId);
        asignaciones.Select(a => a.LocationId).Should().BeEquivalentTo(new[] { sur.Id, norte.Id });
        asignaciones.Single(a => a.IsPrimary).LocationId.Should().Be(norte.Id);
    }

    [Fact]
    public async Task FijarLasSedes_rechazaElConjuntoVacioYLaPrincipalAjena()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");

        (await org.Admin.PutAsJsonAsync($"/api/locations/users/{org.FounderId}", new
        {
            locationIds = Array.Empty<Guid>(), primaryLocationId = sur.Id,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "nadie queda sin sede");

        (await org.Admin.PutAsJsonAsync($"/api/locations/users/{org.FounderId}", new
        {
            locationIds = new[] { sur.Id }, primaryLocationId = org.Principal.Id,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest, "la principal tiene que ser una de sus sedes");
    }

    [Fact]
    public async Task LaInvitacion_dejaAlInvitadoEnLasSedesElegidas()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");

        var resp = await org.Admin.PostAsJsonAsync("/api/invitations", new
        {
            email = "medica.sur@imed.test",
            role = "practitioner",
            locationIds = new[] { sur.Id, org.Principal.Id },
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, await resp.Content.ReadAsStringAsync());
        var invitado = (await resp.Content.ReadFromJsonAsync<InvitationProbe>())!.KeycloakUserId;

        var asignaciones = await AsignacionesAsync(org.OrgId, invitado);
        asignaciones.Should().HaveCount(2);
        asignaciones.Single(a => a.IsPrimary).LocationId.Should().Be(sur.Id, "la primera elegida es la principal");
    }

    [Fact]
    public async Task LaInvitacionSinSedes_dejaAlInvitadoEnLaPrincipal()
    {
        var org = await BootstrapAsync();

        var resp = await org.Admin.PostAsJsonAsync("/api/invitations", new { email = "aux@imed.test", role = "auxiliar" });
        var invitado = (await resp.Content.ReadFromJsonAsync<InvitationProbe>())!.KeycloakUserId;

        (await AsignacionesAsync(org.OrgId, invitado)).Should().ContainSingle()
            .Which.LocationId.Should().Be(org.Principal.Id);
    }

    // ── Sedes del profesional en FHIR ───────────────────────────────────────

    [Fact]
    public async Task LasSedesDelProfesional_lleganASuPractitionerRole_conLaPrincipalPrimero()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");
        var roleId = await ProfesionalAsync(org, org.FounderId, "pract-fundador");

        await org.Admin.PutAsJsonAsync($"/api/locations/users/{org.FounderId}", new
        {
            locationIds = new[] { org.Principal.Id, sur.Id },
            primaryLocationId = sur.Id,
        });

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var dispatcher = scope.ServiceProvider.GetServices<IOutboxDispatcher>()
                .Single(d => d.EventType == PractitionerLocationsSyncEvent.OutboxEventType);
            await dispatcher.DispatchAsync(
                JsonSerializer.Serialize(new PractitionerLocationsSyncEvent.Payload(org.OrgId, org.FounderId)), default);
        }

        fixture.Hapi.PractitionerRoleLocations(roleId).Should().Equal(
            OrganizationLocation.FhirIdFor(sur.Id), OrganizationLocation.FhirIdFor(org.Principal.Id));
    }

    // ── Citas y encuentros ─────────────────────────────────────────────────

    [Fact]
    public async Task UnEncuentroNuevoSinSede_seRechaza()
    {
        var org = await BootstrapAsync();

        var decision = await DecideAsync(org, "POST", "Encounter", Encuentro(sede: null, practitioner: null));

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("sede");
    }

    [Fact]
    public async Task UnaCitaConUnMedicoDeOtraSede_seRechaza()
    {
        var org = await BootstrapAsync();
        var sur = await CrearSedeAsync(org, "SUR", "Sucursal Sur");
        var medicoId = Guid.NewGuid();
        await ProfesionalAsync(org, medicoId, "pract-solo-principal");
        await org.Admin.PostAsJsonAsync($"/api/locations/{org.Principal.Id}/users/{medicoId}", new { makePrimary = true });

        var decision = await DecideAsync(org, "POST", "Appointment", Cita(sur.Id, "pract-solo-principal"));

        decision.Allowed.Should().BeFalse(
            "el médico no trabaja en la Sucursal Sur: la cita se facturaría con una razón social ajena");
        decision.Reason.Should().Contain("Sucursal Sur");
    }

    [Fact]
    public async Task UnaCitaEnUnaSedeDelMedico_pasa()
    {
        var org = await BootstrapAsync();
        var medicoId = Guid.NewGuid();
        await ProfesionalAsync(org, medicoId, "pract-principal");
        await org.Admin.PostAsJsonAsync($"/api/locations/{org.Principal.Id}/users/{medicoId}", new { makePrimary = true });

        var decision = await DecideAsync(org, "POST", "Appointment", Cita(org.Principal.Id, "pract-principal"));

        decision.Allowed.Should().BeTrue(decision.Reason);
    }

    [Fact]
    public async Task UnEncuentroEnUnaSedeDeOtraOrganizacion_seRechaza()
    {
        var org = await BootstrapAsync();
        var ajena = await BootstrapAsync();

        var decision = await DecideAsync(org, "POST", "Encounter", Encuentro(ajena.Principal.Id, practitioner: null));

        decision.Allowed.Should().BeFalse("una referencia no puede habilitar a atender en la sede de otro tenant");
    }

    [Fact]
    public async Task UnEncuentroDentroDeUnBundle_tambienNecesitaSede()
    {
        var org = await BootstrapAsync();
        var bundle = "{\"resourceType\":\"Bundle\",\"type\":\"transaction\",\"entry\":[{\"fullUrl\":\"urn:uuid:1\",\"resource\":"
            + Encuentro(sede: null, practitioner: null)
            + ",\"request\":{\"method\":\"POST\",\"url\":\"Encounter\"}}]}";

        var decision = await DecideAsync(org, "POST", "", bundle);

        decision.Allowed.Should().BeFalse("el Bundle no puede ser la puerta trasera de la regla");
    }

    [Fact]
    public async Task ActualizarUnEncuentroAnteriorSinSede_sigueSiendoPosible()
    {
        // Los encuentros abiertos el día del despliegue no tienen sede. Exigirla al cerrarlos
        // dejaría consultas en curso sin poder terminarse.
        var org = await BootstrapAsync();

        var decision = await DecideAsync(org, "PUT", "Encounter/enc-viejo",
            "{\"resourceType\":\"Encounter\",\"id\":\"enc-viejo\",\"status\":\"completed\",\"subject\":{\"reference\":\"Patient/p1\"}}");

        decision.Allowed.Should().BeTrue(decision.Reason);
    }

    [Fact]
    public async Task EscribirUnLocationDesdeElCliente_seRechaza()
    {
        // La sede se escribe solo por /api/locations: un Location hecho a mano sería una sede que
        // el backend no conoce y con la que no se puede facturar.
        var org = await BootstrapAsync();

        var decision = await DecideAsync(org, "POST", "Location", "{\"resourceType\":\"Location\",\"name\":\"Consultorio 3\"}");

        decision.Allowed.Should().BeFalse();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>Usuario con Practitioner y PractitionerRole, como queda tras completar su perfil.</summary>
    private async Task<string> ProfesionalAsync(Org org, Guid userId, string practitionerFhirId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var orgFhirId = fixture.Hapi.Organizations.Single(o => o.Partition == $"org-{org.OrgId}").FhirId;
        var roleId = await sp.GetRequiredService<IHapiFhirClient>().CreatePractitionerRoleAsync(
            new UpsertPractitionerRoleRequest($"org-{org.OrgId}", practitionerFhirId, orgFhirId, null, null));

        var db = sp.GetRequiredService<AppDbContext>();
        db.UserPractitionerAssignments.Add(
            UserPractitionerAssignment.Create(userId, org.OrgId, practitionerFhirId, roleId));
        await db.SaveChangesAsync();
        return roleId;
    }

    /// <summary>
    /// Decisión del proxy para el request. Se evalúa directo porque la suite corre el proxy en
    /// sombra (FhirProxyTests explica por qué); la regla es lo que se prueba, no el transporte.
    /// </summary>
    private async Task<FhirAccessDecision> DecideAsync(Org org, string method, string url, string body)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionResolver>()
            .ResolveAsync(org.OrgId, ["org_admin", "practitioner"]);
        var descriptor = FhirRequestParser.Parse(method, url);
        descriptor.Should().NotBeNull();
        return await scope.ServiceProvider.GetRequiredService<FhirAccessEvaluator>().EvaluateAsync(
            new FhirCallerContext(org.OrgId, org.FounderId, permissions, null), descriptor!, body);
    }

    private static string Encuentro(Guid? sede, string? practitioner) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["resourceType"] = "Encounter",
        ["status"] = "in-progress",
        ["subject"] = new { reference = "Patient/p1" },
        ["participant"] = practitioner is null ? null : new[] { new { actor = new { reference = $"Practitioner/{practitioner}" } } },
        ["location"] = sede is null ? null : new[] { new { location = new { reference = $"Location/{OrganizationLocation.FhirIdFor(sede.Value)}" } } },
    }.Where(kv => kv.Value is not null).ToDictionary());

    private static string Cita(Guid sede, string practitioner) => JsonSerializer.Serialize(new
    {
        resourceType = "Appointment",
        status = "booked",
        start = "2026-11-02T15:00:00Z",
        end = "2026-11-02T15:30:00Z",
        participant = new object[]
        {
            new { actor = new { reference = "Patient/p1" }, status = "accepted" },
            new { actor = new { reference = $"Practitioner/{practitioner}" }, status = "accepted" },
            new { actor = new { reference = $"Location/{OrganizationLocation.FhirIdFor(sede)}" }, status = "accepted" },
        },
    });

    private sealed record BootstrapProbe(Guid OrganizationId);
    private sealed record InvitationProbe(Guid InvitationId, Guid KeycloakUserId, bool EmailDispatched);
}
