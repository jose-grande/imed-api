using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Organizations;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Team;

/// <summary>
/// "Atiende pacientes": el administrador que además es médico (el dueño de una clínica chica) se
/// marca en Mi equipo. Decide si aparece en la agenda y si se le pide su perfil profesional.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class SeesPatientsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record BootstrapResponseDto(Guid OrganizationId);

    private async Task<(Guid OrgId, HttpClient Admin)> OrganizacionAsync()
    {
        await Task.Delay(500);
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "founder", "founder@imed.test");
        var boot = await fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "founder"))
            .PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Nelson", nationalTaxId = "0801-1990-77777" });
        boot.StatusCode.Should().Be(HttpStatusCode.OK);
        var orgId = (await boot.Content.ReadFromJsonAsync<BootstrapResponseDto>())!.OrganizationId;
        return (orgId, fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            founderId, organizationId: orgId.ToString(), realmRoles: ["org_admin"])));
    }

    /// <summary>Miembro con estos roles; con perfil clínico si se pide.</summary>
    private async Task<Guid> MiembroAsync(Guid orgId, bool conPerfil, params string[] roles)
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, $"u{userId:N}", $"u{userId:N}@imed.test");
        using var scope = fixture.Factory.Services.CreateScope();
        var kc = scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>();
        foreach (var r in roles)
        {
            await kc.AssignRealmRoleAsync(userId, r);
        }
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OrganizationMembers.Add(OrganizationMember.Join(userId, orgId, $"Miembro {userId:N}", null));
        if (conPerfil)
        {
            db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(userId, orgId, $"prac-{userId:N}"));
        }
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<bool?> MarcaAsync(Guid orgId, Guid userId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.OrganizationMembers.AsNoTracking()
            .SingleAsync(m => m.OrganizationId == orgId && m.KeycloakUserId == userId)).SeesPatients;
    }

    private static async Task<TeamMember> EnEquipoAsync(HttpClient admin, Guid userId) =>
        (await admin.GetFromJsonAsync<TeamMember[]>("/api/team"))!.Single(m => m.KeycloakUserId == userId);

    [Fact]
    public async Task ElAdministrador_seMarcaComoQueAtiende_yEquipoLoMuestra()
    {
        var (orgId, admin) = await OrganizacionAsync();
        var nelson = await MiembroAsync(orgId, conPerfil: true, "org_admin");

        // Sin marca y con un perfil sin especialidad ni colegiación: no atiende.
        (await EnEquipoAsync(admin, nelson)).SeesPatients.Should().BeFalse();

        var resp = await admin.PutAsJsonAsync($"/api/team/{nelson}/sees-patients", new { seesPatients = true });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        (await MarcaAsync(orgId, nelson)).Should().BeTrue();
        (await EnEquipoAsync(admin, nelson)).SeesPatients.Should().BeTrue();

        await admin.PutAsJsonAsync($"/api/team/{nelson}/sees-patients", new { seesPatients = false });
        (await EnEquipoAsync(admin, nelson)).SeesPatients.Should().BeFalse();
    }

    [Theory]
    [InlineData("practitioner")]
    [InlineData("auxiliar")]
    public async Task LaMarca_esSoloParaAdministradores(string rol)
    {
        var (orgId, admin) = await OrganizacionAsync();
        var miembro = await MiembroAsync(orgId, conPerfil: true, rol);

        var resp = await admin.PutAsJsonAsync($"/api/team/{miembro}/sees-patients", new { seesPatients = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await MarcaAsync(orgId, miembro)).Should().BeNull();
    }

    [Fact]
    public async Task ElMedico_siempreAtiende_yElPersonalNunca()
    {
        var (orgId, admin) = await OrganizacionAsync();
        var medico = await MiembroAsync(orgId, conPerfil: true, "practitioner");
        var tecnico = await MiembroAsync(orgId, conPerfil: true, "lab_tech");

        (await EnEquipoAsync(admin, medico)).SeesPatients.Should().BeTrue();
        (await EnEquipoAsync(admin, tecnico)).SeesPatients.Should().BeFalse();
    }

    [Fact]
    public async Task ElMedicoQuePasaAAdministrador_sigueAtendiendo()
    {
        var (orgId, admin) = await OrganizacionAsync();
        var medico = await MiembroAsync(orgId, conPerfil: true, "practitioner");

        var resp = await admin.PutAsJsonAsync($"/api/team/{medico}/role", new { role = "org_admin" });
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());

        (await MarcaAsync(orgId, medico)).Should().BeTrue();
        (await EnEquipoAsync(admin, medico)).SeesPatients.Should().BeTrue();
    }

    [Fact]
    public async Task ElAvisoDePerfilIncompleto_soloAQuienLoNecesita()
    {
        var (orgId, admin) = await OrganizacionAsync();

        async Task<StartSessionResult> SesionAsync(Guid userId, params string[] roles)
        {
            var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
                userId, organizationId: orgId.ToString(), realmRoles: roles));
            var resp = await client.PostAsync("/api/me/session", null);
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            return (await resp.Content.ReadFromJsonAsync<StartSessionResult>())!;
        }

        // Caja y el administrador que no atiende no necesitan perfil profesional.
        var cajero = await MiembroAsync(orgId, conPerfil: false, "cashier");
        (await SesionAsync(cajero, "cashier")).NeedsClinicalProfile.Should().BeFalse();

        var gerente = await MiembroAsync(orgId, conPerfil: false, "org_admin");
        (await SesionAsync(gerente, "org_admin")).NeedsClinicalProfile.Should().BeFalse();

        // El administrador que atiende, sin perfil todavía, sí.
        await admin.PutAsJsonAsync($"/api/team/{gerente}/sees-patients", new { seesPatients = true });
        (await SesionAsync(gerente, "org_admin")).NeedsClinicalProfile.Should().BeTrue();
    }
}
