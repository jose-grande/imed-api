using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Invitations;
using IMed.Api.Domain.Invitations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Invitations;

/// <summary>
/// Tests del lazy provision al primer login del invitee:
/// <list type="bullet">
///   <item>Happy path: GET /api/me/practitioner-profile → detecta invitation
///   pending por email + crea Practitioner+PractitionerRole en HAPI + acepta
///   invitation + sync KC.</item>
///   <item>HAPI down: outbox <c>invitation.lazy-provision-pending</c> + retry
///   idempotente cuando HAPI vuelve.</item>
/// </list>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LazyProvisionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetMyPractitionerProfile_con_invitation_pending_completa_lazy_provision()
    {
        // Arrange: bootstrap org + invite a un email.
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica LazyOK");

        var inviteResp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "newdoc@imed.test",
            role = "practitioner",
            specialtyHint = "394814009" // Medicina general (debe estar en specialty_catalog seeded).
        });
        inviteResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var inviteBody = await inviteResp.Content.ReadFromJsonAsync<CreateInvitationResponseDto>();
        var inviteeKcUserId = inviteBody!.KeycloakUserId;

        var hapiCreatesBefore = fixture.Hapi.CreatePractitionerCallCount;

        // Act: el invitee loguea por primera vez. Su JWT trae email + organization_id.
        var inviteeToken = fixture.Keycloak.IssueUserToken(
            inviteeKcUserId,
            organizationId: orgId.ToString(),
            preferredUsername: "newdoc",
            email: "newdoc@imed.test",
            givenName: "Nuevo",
            familyName: "Médico",
            realmRoles: ["practitioner"]);
        var inviteeClient = fixture.CreateClientWithToken(inviteeToken);
        var profileResp = await inviteeClient.GetAsync("/api/me/practitioner-profile");

        // Assert: 200 con perfil completo (Configured=true).
        profileResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await profileResp.Content.ReadFromJsonAsync<PractitionerProfileResponse>();
        dto.Should().NotBeNull();
        dto!.Configured.Should().BeTrue("lazy provision creó Practitioner + assignment");
        dto.LazyProvisionInProgress.Should().BeFalse();
        dto.PractitionerFhirId.Should().NotBeNullOrEmpty();
        dto.PractitionerRoleFhirId.Should().NotBeNullOrEmpty("specialtyHint estaba presente");
        dto.SpecialtyCode.Should().Be("394814009");

        fixture.Hapi.CreatePractitionerCallCount.Should().Be(hapiCreatesBefore + 1);
        fixture.Hapi.CreatePractitionerRoleCallCount.Should().BeGreaterThan(0);

        // BD: invitation aceptada + assignment creado + KC user practitioner_id seteado.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var invitation = await db.Invitations.SingleAsync();
        invitation.Status.Should().Be(InvitationStatus.Accepted);
        invitation.AcceptedAt.Should().NotBeNull();
        invitation.KeycloakUserId.Should().Be(inviteeKcUserId);

        var assignment = await db.UserPractitionerAssignments
            .SingleAsync(a => a.KeycloakUserId == inviteeKcUserId && a.OrganizationId == orgId);
        assignment.PractitionerFhirId.Should().Be(dto.PractitionerFhirId);

        var kcAttrs = fixture.Keycloak.GetUserAttributes(inviteeKcUserId);
        kcAttrs.Should().ContainKey("practitioner_id");
        kcAttrs["practitioner_id"].Should().ContainSingle().Which.Should().Be(dto.PractitionerFhirId);

        // Audit log "completed".
        var audit = await db.AppAuditLog
            .Where(e => e.Category == "invitations" && e.Action == "completed")
            .SingleAsync();
        audit.ActorUserId.Should().Be(inviteeKcUserId);
    }

    [Fact]
    public async Task GetMyPractitionerProfile_con_HAPI_down_persiste_outbox_y_retry_completa()
    {
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica LazyHapiDown");

        var inviteResp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "retrydoc@imed.test",
            role = "practitioner"
        });
        var inviteBody = await inviteResp.Content.ReadFromJsonAsync<CreateInvitationResponseDto>();
        var inviteeKcUserId = inviteBody!.KeycloakUserId;

        // Forzamos que HAPI POST /Practitioner falle 1 vez. El primer GET del
        // perfil dispara el lazy provision → falla → outbox + LazyProvisionInProgress=true.
        // El worker reintenta y completa cuando HAPI vuelve.
        fixture.Hapi.EnableCreatePractitionerFailing(count: 1);

        var inviteeToken = fixture.Keycloak.IssueUserToken(
            inviteeKcUserId,
            organizationId: orgId.ToString(),
            preferredUsername: "retrydoc",
            email: "retrydoc@imed.test",
            givenName: "Retry",
            familyName: "Doc");
        var inviteeClient = fixture.CreateClientWithToken(inviteeToken);

        var firstResp = await inviteeClient.GetAsync("/api/me/practitioner-profile");
        firstResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstDto = await firstResp.Content.ReadFromJsonAsync<PractitionerProfileResponse>();
        firstDto!.Configured.Should().BeFalse();
        firstDto.LazyProvisionInProgress.Should().BeTrue("HAPI cayó durante el primer intento");

        // Outbox event creado.
        var ev = await fixture.FindOutboxByTypeAsync(InvitationLazyProvisionPendingEvent.OutboxEventType);
        ev.Should().NotBeNull();

        // Esperamos que el worker complete: el outbox event queda dispatched y
        // la invitation pasa a accepted.
        await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync(InvitationLazyProvisionPendingEvent.OutboxEventType),
            predicate: e => e?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(30),
            description: "outbox invitation.lazy-provision-pending dispatched");

        // Re-pegar el GET — ahora debe devolver Configured=true.
        var retryResp = await inviteeClient.GetAsync("/api/me/practitioner-profile");
        retryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var retryDto = await retryResp.Content.ReadFromJsonAsync<PractitionerProfileResponse>();
        retryDto!.Configured.Should().BeTrue("worker completó el lazy provision");
        retryDto.PractitionerFhirId.Should().NotBeNullOrEmpty();
    }

    // -------------------------------------------------------------------------

    private async Task<(Guid orgId, string orgFhirId, HttpClient client, Guid adminUserId)>
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

        var orgFhirId = fixture.Hapi.Organizations.Single(o => o.Partition == $"org-{body!.OrganizationId}").FhirId;

        var adminToken = fixture.Keycloak.IssueUserToken(
            founderId,
            organizationId: body!.OrganizationId.ToString(),
            preferredUsername: "founder",
            realmRoles: ["org_admin"]);
        var client = fixture.CreateClientWithToken(adminToken);
        return (body.OrganizationId, orgFhirId, client, founderId);
    }

    private sealed record BootstrapResponseDto(Guid OrganizationId, string AssignedTier, bool PendingKeycloakSync);

    private sealed record CreateInvitationResponseDto(Guid InvitationId, Guid KeycloakUserId, bool EmailDispatched);

    private sealed record PractitionerProfileResponse(
        bool Configured,
        string? PractitionerFhirId,
        string? PractitionerRoleFhirId,
        string? Family,
        IReadOnlyList<string>? Given,
        string? SpecialtyCode,
        string? SpecialtyDisplay,
        bool LazyProvisionInProgress);
}
