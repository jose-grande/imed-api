using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Team;

/// <summary>
/// Funciones adicionales al rol principal (plan de salida a producción, A1): cajero, facturador,
/// laboratorio, farmacia, inventario. Se dan y se quitan desde Mi equipo.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class SetMemberFunctionsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PUT_functions_reemplazaLasFunciones_ySinTocarElRolPrincipal()
    {
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica Funciones");
        var memberId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(memberId, "member", "member@imed.test");
        await SeedAssignmentAndRoleAsync(memberId, orgId, "auxiliar");

        var r1 = await adminClient.PutAsJsonAsync($"/api/team/{memberId}/functions",
            new { functions = new[] { "cashier", "lab_tech", "cashier" } });
        r1.StatusCode.Should().Be(HttpStatusCode.OK, await r1.Content.ReadAsStringAsync());
        var b1 = await r1.Content.ReadFromJsonAsync<FunctionsResponseDto>();
        b1!.Functions.Should().Equal("cashier", "lab_tech");
        fixture.Keycloak.GetUserRoles(memberId).Should().Contain(["auxiliar", "cashier", "lab_tech"]);

        // Reemplazo: lo que no viene se quita.
        var r2 = await adminClient.PutAsJsonAsync($"/api/team/{memberId}/functions",
            new { functions = new[] { "pharmacist" } });
        var b2 = await r2.Content.ReadFromJsonAsync<FunctionsResponseDto>();
        b2!.Added.Should().Equal("pharmacist");
        b2.Removed.Should().BeEquivalentTo(["cashier", "lab_tech"]);

        var roles = fixture.Keycloak.GetUserRoles(memberId);
        roles.Should().Contain(["auxiliar", "pharmacist"]);
        roles.Should().NotContain(["cashier", "lab_tech"]);
    }

    [Fact]
    public async Task PUT_functions_noPuedeDarUnRolPrincipal()
    {
        // Dar org_admin por esta vía saltaría las reglas del cambio de rol (último administrador).
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica Escalada");
        var memberId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(memberId, "member", "member@imed.test");
        await SeedAssignmentAndRoleAsync(memberId, orgId, "practitioner");

        var resp = await adminClient.PutAsJsonAsync($"/api/team/{memberId}/functions",
            new { functions = new[] { "org_admin" } });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Keycloak.GetUserRoles(memberId).Should().NotContain("org_admin");
    }

    [Fact]
    public async Task PUT_functions_fueraDeLaOrg_devuelve404_ySoloElAdministradorPuede()
    {
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica Ajena");
        var foreignerId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(foreignerId, "foreign", "foreign@imed.test");

        (await adminClient.PutAsJsonAsync($"/api/team/{foreignerId}/functions", new { functions = new[] { "cashier" } }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var cajero = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(cajero, "cajero", "cajero@imed.test");
        var cajeroClient = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            cajero, organizationId: orgId.ToString(), realmRoles: ["cashier"]));
        (await cajeroClient.PutAsJsonAsync($"/api/team/{cajero}/functions", new { functions = new[] { "billing_supervisor" } }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public void LasFunciones_seNormalizanEnElOrdenDelCatalogo()
    {
        // La lista del equipo y el reemplazo usan este orden: el mismo conjunto se ve siempre igual.
        IMed.Api.Domain.Authorization.FunctionalRoles.Normalize(["warehouse_keeper", "cashier", "cashier"])
            .Should().Equal("cashier", "warehouse_keeper");

        var invalida = () => IMed.Api.Domain.Authorization.FunctionalRoles.Normalize(["practitioner"]);
        invalida.Should().Throw<ArgumentException>().WithMessage("*practitioner*");
    }

    // -------------------------------------------------------------------------

    private async Task<(Guid orgId, string orgFhirId, HttpClient adminClient, Guid founderId)>
        BootstrapOrgAsync(string orgName)
    {
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "founder", "founder@imed.test");
        var bootstrapToken = fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "founder");
        var bootstrapClient = fixture.CreateClientWithToken(bootstrapToken);

        var bootstrapResp = await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = orgName,
            nationalTaxId = "0801-1990-99999"
        });
        bootstrapResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await bootstrapResp.Content.ReadFromJsonAsync<BootstrapResponseDto>();

        // Bootstrap también seedeó UserPractitionerAssignment? NO — sólo
        // UserTierAssignment + role org_admin. Para el lock-out check, además
        // necesitamos un assignment en imed_app (que se confirme pertenencia).
        await SeedAssignmentForFounderAsync(founderId, body!.OrganizationId);

        var orgFhirId = fixture.Hapi.Organizations.Single(o => o.Partition == $"org-{body.OrganizationId}").FhirId;

        var adminToken = fixture.Keycloak.IssueUserToken(
            founderId,
            organizationId: body.OrganizationId.ToString(),
            preferredUsername: "founder",
            realmRoles: ["org_admin"]);
        var adminClient = fixture.CreateClientWithToken(adminToken);
        return (body.OrganizationId, orgFhirId, adminClient, founderId);
    }

    private async Task SeedAssignmentAndRoleAsync(Guid userId, Guid orgId, string realmRole)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(
            keycloakUserId: userId,
            organizationId: orgId,
            practitionerFhirId: $"prac-{userId:N}"));
        await db.SaveChangesAsync();

        var kc = scope.ServiceProvider.GetRequiredService<IMed.Api.Application.Abstractions.IKeycloakAdminClient>();
        await kc.AssignRealmRoleAsync(userId, realmRole);
    }

    private async Task SeedAssignmentForFounderAsync(Guid founderId, Guid orgId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(
            keycloakUserId: founderId,
            organizationId: orgId,
            practitionerFhirId: $"prac-{founderId:N}"));
        await db.SaveChangesAsync();
    }

    private sealed record BootstrapResponseDto(Guid OrganizationId, string AssignedTier, bool PendingKeycloakSync);

    private sealed record FunctionsResponseDto(
        Guid TargetKeycloakUserId,
        List<string> Functions,
        List<string> Added,
        List<string> Removed,
        bool PendingKeycloakSync);
}
