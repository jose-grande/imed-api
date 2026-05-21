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

[Collection(ImedApiCollection.Name)]
public sealed class ChangeMemberRoleTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PUT_role_happy_path_swaps_kc_role()
    {
        var (orgId, _, adminClient, founderId) = await BootstrapOrgAsync("Clínica RoleSwap");

        // Pre-seed: un member como practitioner.
        var memberId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(memberId, "member", "member@imed.test");
        await SeedAssignmentAndRoleAsync(memberId, orgId, "practitioner");

        var resp = await adminClient.PutAsJsonAsync(
            $"/api/team/{memberId}/role",
            new { role = "auxiliar" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<RoleChangeResponseDto>();
        body!.OldRole.Should().Be("practitioner");
        body.NewRole.Should().Be("auxiliar");
        body.PendingKeycloakSync.Should().BeFalse();

        // KC: el member tiene auxiliar y NO practitioner.
        var roles = fixture.Keycloak.GetUserRoles(memberId);
        roles.Should().Contain("auxiliar");
        roles.Should().NotContain("practitioner");

        _ = founderId;
    }

    [Fact]
    public async Task PUT_role_unico_admin_intenta_degradarse_devuelve_409()
    {
        var (_, _, adminClient, founderId) = await BootstrapOrgAsync("Clínica Lock-out");

        // Únicamente el founder es org_admin de la org. El intento de auto-degradarse
        // debe rechazarse para evitar lock-out.
        var resp = await adminClient.PutAsJsonAsync(
            $"/api/team/{founderId}/role",
            new { role = "practitioner" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>();
        problem!.Detail.Should().Contain("único administrador");
    }

    [Fact]
    public async Task PUT_role_target_fuera_de_org_devuelve_404()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica Aislada");

        // User en KC pero sin assignment en esta org.
        var foreignerId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(foreignerId, "foreign", "foreign@imed.test");

        var resp = await adminClient.PutAsJsonAsync(
            $"/api/team/{foreignerId}/role",
            new { role = "practitioner" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PUT_role_cuando_KC_remove_falla_outbox_completa_retry()
    {
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica RetryRole");

        var memberId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(memberId, "member", "member@imed.test");
        await SeedAssignmentAndRoleAsync(memberId, orgId, "practitioner");

        // Forzamos que KC DELETE role-mappings falle 1 vez. El handler reporta
        // PendingKeycloakSync=true y el worker eventualmente completa.
        fixture.Keycloak.EnableDeleteRoleFailing(count: 1);

        var resp = await adminClient.PutAsJsonAsync(
            $"/api/team/{memberId}/role",
            new { role = "auxiliar" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<RoleChangeResponseDto>();
        body!.PendingKeycloakSync.Should().BeTrue();

        // Outbox event creado.
        var ev = await fixture.FindOutboxByTypeAsync(TeamRoleChangedEvent.OutboxEventType);
        ev.Should().NotBeNull();

        // Worker reintenta + completa.
        await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync(TeamRoleChangedEvent.OutboxEventType),
            predicate: e => e?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(20),
            description: "outbox team.role-changed dispatched");

        // Estado final en KC: auxiliar asignado, practitioner removido.
        var rolesAfter = fixture.Keycloak.GetUserRoles(memberId);
        rolesAfter.Should().Contain("auxiliar");
        rolesAfter.Should().NotContain("practitioner");
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

    private sealed record RoleChangeResponseDto(
        Guid TargetKeycloakUserId,
        string OldRole,
        string NewRole,
        bool PendingKeycloakSync);

    private sealed record ProblemPayload(string Title, string Detail);
}
