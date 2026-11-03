using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Invitations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Team;

/// <summary>
/// Membresía registrada al iniciar sesión (observación de staging: el administrador no veía a
/// los demás usuarios ni sus sedes). Un usuario con <c>organization_id</c> —también uno creado
/// desde la consola de Keycloak— se registra en su primer ingreso; solo los roles clínicos y de
/// laboratorio reciben perfil clínico (Practitioner).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class MemberSessionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record BootstrapResponseDto(Guid OrganizationId, string AssignedTier, bool PendingKeycloakSync);

    /// <summary>Organización nueva; devuelve su id y un cliente del fundador (org_admin).</summary>
    private async Task<(Guid OrgId, Guid FounderId, HttpClient Admin)> OrganizacionAsync()
    {
        await Task.Delay(500);
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "founder", "founder@imed.test");
        var boot = await fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "founder"))
            .PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Sesión", nationalTaxId = "0801-1990-55555" });
        boot.StatusCode.Should().Be(HttpStatusCode.OK);
        var orgId = (await boot.Content.ReadFromJsonAsync<BootstrapResponseDto>())!.OrganizationId;
        var admin = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            founderId, organizationId: orgId.ToString(), realmRoles: ["org_admin"]));
        return (orgId, founderId, admin);
    }

    /// <summary>Una cuenta creada "desde la consola de Keycloak": organization_id y roles, nada más.</summary>
    private async Task<HttpClient> CuentaDeConsolaAsync(Guid userId, Guid orgId, string username, params string[] roles)
    {
        fixture.Keycloak.RegisterUser(userId, username, $"{username}@imed.test");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var kc = scope.ServiceProvider.GetRequiredService<IMed.Api.Application.Abstractions.IKeycloakAdminClient>();
            foreach (var rol in roles)
            {
                await kc.AssignRealmRoleAsync(userId, rol);
            }
        }
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: roles, preferredUsername: username,
            email: $"{username}@imed.test", givenName: username, familyName: "Prueba"));
    }

    private static async Task<StartSessionResult> SesionAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/api/me/session", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<StartSessionResult>())!;
    }

    [Fact]
    public async Task CuentaDeConsolaNoClinica_apareceEnEquipo_sinPerfilClinico_yConSedeAsignable()
    {
        var (orgId, _, admin) = await OrganizacionAsync();
        var cajeroId = Guid.NewGuid();
        var cajero = await CuentaDeConsolaAsync(cajeroId, orgId, "cajero", "cashier");

        // Antes de su primer ingreso no existe para la organización.
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!
            .Should().NotContain(m => m.KeycloakUserId == cajeroId);

        var sesion = await SesionAsync(cajero);
        sesion.HasClinicalProfile.Should().BeFalse("cajero no es un rol clínico ni de laboratorio");
        var practitionersAntes = fixture.Hapi.CreatePractitionerCallCount;

        var miembro = (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Single(m => m.KeycloakUserId == cajeroId);
        miembro.PractitionerFhirId.Should().BeNull();
        miembro.Email.Should().Be("cajero@imed.test");
        miembro.Functions.Should().Contain("cashier");

        // Y se le puede asignar sede, que era la otra mitad de la queja.
        var sede = (await admin.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single(l => l.IsDefault);
        (await admin.PutAsJsonAsync($"/api/locations/users/{cajeroId}",
                new { locationIds = new[] { sede.Id }, primaryLocationId = sede.Id }))
            .IsSuccessStatusCode.Should().BeTrue();
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Single(m => m.KeycloakUserId == cajeroId)
            .Locations.Should().ContainSingle(l => l.Id == sede.Id);

        // Un segundo ingreso no duplica nada ni crea un profesional.
        await SesionAsync(cajero);
        fixture.Hapi.CreatePractitionerCallCount.Should().Be(practitionersAntes);
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Count(m => m.KeycloakUserId == cajeroId).Should().Be(1);
    }

    [Theory]
    [InlineData("practitioner")]
    [InlineData("bioanalyst")]
    public async Task CuentaDeConsolaClinicaODeLaboratorio_recibePerfilClinico_unaSolaVez(string rol)
    {
        var (orgId, _, admin) = await OrganizacionAsync();
        var userId = Guid.NewGuid();
        var cliente = await CuentaDeConsolaAsync(userId, orgId, "profesional", rol);
        var antes = fixture.Hapi.CreatePractitionerCallCount;

        (await SesionAsync(cliente)).HasClinicalProfile.Should().BeTrue();
        (await SesionAsync(cliente)).HasClinicalProfile.Should().BeTrue();

        fixture.Hapi.CreatePractitionerCallCount.Should().Be(antes + 1, "el perfil se crea una sola vez");
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Single(m => m.KeycloakUserId == userId)
            .PractitionerFhirId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task InvitacionPendienteSinRolClinico_seAceptaAlIniciarSesion_sinCrearProfesional()
    {
        var (orgId, founderId, _) = await OrganizacionAsync();
        var userId = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orgFhirId = (await scope.ServiceProvider
                .GetRequiredService<IMed.Api.Application.Profile.IOrganizationProfileRepository>()
                .GetByIdAsync(orgId))!.FhirId;
            db.Invitations.Add(Invitation.Create(
                "admin2@imed.test", orgId, orgFhirId, InvitationRole.OrgAdmin, null, founderId));
            await db.SaveChangesAsync();
        }
        var antes = fixture.Hapi.CreatePractitionerCallCount;

        var sesion = await SesionAsync(await CuentaDeConsolaAsync(userId, orgId, "admin2", "org_admin"));

        sesion.HasClinicalProfile.Should().BeFalse();
        fixture.Hapi.CreatePractitionerCallCount.Should().Be(antes);
        using var check = fixture.Factory.Services.CreateScope();
        (await check.ServiceProvider.GetRequiredService<AppDbContext>().Invitations.SingleAsync(i => i.Email == "admin2@imed.test"))
            .Status.Should().Be(InvitationStatus.Accepted);
    }

    [Fact]
    public async Task Baja_bloqueaElIngreso_deshabilitaEnKeycloak_yReactivarLoRevierte()
    {
        var (orgId, founderId, admin) = await OrganizacionAsync();
        var userId = Guid.NewGuid();
        var cliente = await CuentaDeConsolaAsync(userId, orgId, "recepcion", "auxiliar");
        await SesionAsync(cliente);

        (await admin.PutAsJsonAsync($"/api/team/{userId}/active", new { active = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Keycloak.IsUserEnabled(userId).Should().BeFalse();
        var rechazado = await cliente.PostAsync("/api/me/session", null);
        rechazado.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await rechazado.Content.ReadAsStringAsync()).Should().Contain("member_inactive");
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Single(m => m.KeycloakUserId == userId)
            .IsActive.Should().BeFalse("el administrador lo sigue viendo para poder reactivarlo");

        (await admin.PutAsJsonAsync($"/api/team/{userId}/active", new { active = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Keycloak.IsUserEnabled(userId).Should().BeTrue();
        await SesionAsync(cliente);

        // Nadie se da de baja a sí mismo.
        var self = await admin.PutAsJsonAsync($"/api/team/{founderId}/active", new { active = false });
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SinOrganizacion_noHayNadaQueRegistrar()
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var resp = await fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(userId))
            .PostAsync("/api/me/session", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
