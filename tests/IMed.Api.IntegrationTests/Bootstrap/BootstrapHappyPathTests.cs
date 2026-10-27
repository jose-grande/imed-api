using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Subscriptions;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using IMed.Api.Infrastructure.Persistence;
using Xunit;

namespace IMed.Api.IntegrationTests.Bootstrap;

[Collection(ImedApiCollection.Name)]
public sealed class BootstrapHappyPathTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Bootstrap_happy_path_creates_partition_org_and_syncs_keycloak()
    {
        // Arrange: registrar al usuario en el "directorio" de Keycloak.
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "founder", "founder@imed.test");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "founder");
        var client = fixture.CreateClientWithToken(token);

        // Act: POST /api/organizations/bootstrap
        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica Acme",
            nationalTaxId = "0801-1990-12345"
        });

        // Assert: respuesta 200 con flag pendingKeycloakSync = false (sync OK).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        body.Should().NotBeNull();
        body!.AssignedTier.Should().Be("BASIC");
        body.PendingKeycloakSync.Should().BeFalse();
        body.OrganizationId.Should().NotBe(Guid.Empty);

        // HAPI: una partition + una organization.
        fixture.Hapi.CreatedPartitions.Should().ContainSingle()
            .Which.Should().Be($"org-{body.OrganizationId}");
        fixture.Hapi.Organizations.Should().ContainSingle()
            .Which.Partition.Should().Be($"org-{body.OrganizationId}");

        // Keycloak: attrs + role asignados.
        var attrs = fixture.Keycloak.GetUserAttributes(userId);
        attrs.Should().ContainKey("organization_id");
        attrs["organization_id"].Should().ContainSingle().Which.Should().Be(body.OrganizationId.ToString());
        attrs["subscription_tier"].Should().ContainSingle().Which.Should().Be("BASIC");
        fixture.Keycloak.GetUserRoles(userId).Should().Contain("org_admin");

        // imed_app: subscription, user_tier_assignment, audit log y outbox event dispatched.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sub = await db.OrganizationSubscriptions.SingleAsync();
        sub.OrganizationId.Should().Be(body.OrganizationId);
        sub.ContractStatus.Should().Be(ContractStatus.Trial);

        var tierAssignment = await db.UserTierAssignments.SingleAsync();
        tierAssignment.KeycloakUserId.Should().Be(userId);
        tierAssignment.Tier.Should().Be(SubscriptionTier.Basic);

        var auditEntries = await db.AppAuditLog.ToListAsync();
        auditEntries.Should().Contain(e =>
            e.Category == "organization" && e.Action == "bootstrap_started" && e.ActorUserId == userId);
        // Cuando el sync es OK in-line, el outbox.dispatched lo registra el worker la próxima vez.
        // No lo exigimos en el happy path porque el evento se marca dispatched ANTES de que el
        // worker lo procese — el worker no lo verá pendiente.

        // Solo el evento del alta: desde FAC-9 el alta también encola la proyección de la sede
        // principal y de las sedes del fundador, que no son lo que esta prueba mira.
        var outbox = await db.OutboxEvents.SingleAsync(e => e.Type == "organization.bootstrapped");
        outbox.DispatchedAt.Should().NotBeNull("el hot-path marcó el evento dispatched");
        outbox.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_my_profile_returns_organization_after_bootstrap()
    {
        // Arrange: usuario sin org, hace bootstrap.
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "founder2");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "founder2");
        var client = fixture.CreateClientWithToken(token);

        var bootstrapResponse = await client.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica Beta"
        });
        bootstrapResponse.EnsureSuccessStatusCode();
        var body = await bootstrapResponse.Content.ReadFromJsonAsync<BootstrapResponse>();

        // Re-issue token con el organization_id para simular el re-login.
        var tokenAfterLogin = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: body!.OrganizationId.ToString(),
            subscriptionTier: "BASIC",
            preferredUsername: "founder2",
            realmRoles: ["org_admin"]);
        var clientWithOrg = fixture.CreateClientWithToken(tokenAfterLogin);

        // Act
        var profileResp = await clientWithOrg.GetAsync("/api/me");

        // Assert
        profileResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await profileResp.Content.ReadFromJsonAsync<MyProfileResponseDto>();
        profile.Should().NotBeNull();
        profile!.User.KeycloakUserId.Should().Be(userId);
        profile.User.Roles.Should().Contain("org_admin");
        profile.Organization.Should().NotBeNull();
        profile.Organization!.Id.Should().Be(body.OrganizationId);
        profile.Organization.Name.Should().Be("Clínica Beta");
        // El repo serializa el enum como string upper (ya consumido así por el frontend).
        profile.Organization.ContractStatus.Should().Be("TRIAL");
    }

    [Fact]
    public async Task Bootstrap_rejects_user_that_already_belongs_to_an_organization()
    {
        var userId = Guid.NewGuid();
        var existingOrgId = Guid.NewGuid().ToString();
        fixture.Keycloak.RegisterUser(userId, "intruder");
        // Token YA tiene organization_id → bootstrap debe rechazar con 409.
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: existingOrgId,
            preferredUsername: "intruder");
        var client = fixture.CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Hostile Clinic"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fixture.Hapi.CreatedPartitions.Should().BeEmpty("nada debería haberse creado");
    }

    private sealed record BootstrapResponse(
        Guid OrganizationId,
        string OrganizationFhirId,
        string AssignedTier,
        bool PendingKeycloakSync,
        string Message);

    private sealed record MyProfileResponseDto(UserDto User, OrgDto? Organization);
    private sealed record UserDto(Guid KeycloakUserId, string? Name, string? Email, string[] Roles);
    private sealed record OrgDto(Guid Id, string Name, string FhirId, string ContractStatus, DateTimeOffset? TrialEndsAt);
}
