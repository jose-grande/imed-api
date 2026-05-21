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

[Collection(ImedApiCollection.Name)]
public sealed class InvitationsEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task POST_invitations_happy_path_creates_kc_user_and_sends_email()
    {
        // Arrange: bootstrap una org y obtener token de org_admin.
        var (orgId, _, adminClient, adminUserId) = await BootstrapOrgAsync("Clínica Invitaciones");

        // Act: invitar a colaborator@imed.test como practitioner.
        var inviteResp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "colaborator@imed.test",
            role = "practitioner",
            specialtyHint = (string?)null
        });

        // Assert: 201 Created, KC user creado, email disparado.
        inviteResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await inviteResp.Content.ReadFromJsonAsync<CreateInvitationResponseDto>();
        body.Should().NotBeNull();
        body!.EmailDispatched.Should().BeTrue();

        fixture.Keycloak.CreateUserCallCount.Should().Be(1);
        fixture.Keycloak.ExecuteActionsEmailCallCount.Should().Be(1);
        fixture.Keycloak.LastExecuteActionsEmail.Should().NotBeNull();
        fixture.Keycloak.LastExecuteActionsEmail!.UserId.Should().Be(body.KeycloakUserId);
        fixture.Keycloak.LastExecuteActionsEmail.Actions.Should().BeEquivalentTo(["UPDATE_PASSWORD", "VERIFY_EMAIL"]);
        fixture.Keycloak.LastExecuteActionsEmail.LifespanSeconds.Should().Be(7 * 24 * 3600);

        // Realm role asignado en KC.
        fixture.Keycloak.GetUserRoles(body.KeycloakUserId).Should().Contain("practitioner");

        // BD: invitation persistida pending.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inv = await db.Invitations.SingleAsync();
        inv.Email.Should().Be("colaborator@imed.test");
        inv.Status.Should().Be(InvitationStatus.Pending);
        inv.Role.Should().Be(InvitationRole.Practitioner);
        inv.OrganizationId.Should().Be(orgId);
        inv.CreatedBy.Should().Be(adminUserId);
        inv.KeycloakUserId.Should().Be(body.KeycloakUserId);

        // Audit log entry "created".
        var audit = await db.AppAuditLog
            .Where(e => e.Category == "invitations" && e.Action == "created")
            .SingleAsync();
        audit.OrganizationId.Should().Be(orgId);
        audit.ActorUserId.Should().Be(adminUserId);
    }

    [Fact]
    public async Task POST_invitations_email_duplicado_pending_devuelve_409()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica Dup");

        var first = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "dup@imed.test",
            role = "practitioner"
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "dup@imed.test",
            role = "practitioner"
        });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task POST_invitations_email_ya_es_miembro_devuelve_409()
    {
        var (orgId, _, adminClient, _) = await BootstrapOrgAsync("Clínica Miembro");

        // Pre-seed: un usuario que ya es miembro de la org (assignment + KC user).
        var memberId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(memberId, "member", "member@imed.test");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserPractitionerAssignments.Add(IMed.Api.Domain.Practitioners.UserPractitionerAssignment.Create(
                keycloakUserId: memberId,
                organizationId: orgId,
                practitionerFhirId: "prac-member"));
            await db.SaveChangesAsync();
        }

        var resp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "member@imed.test",
            role = "practitioner"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>();
        problem!.Detail.Should().Contain("ya es miembro");
    }

    [Fact]
    public async Task POST_invitations_email_en_otra_org_devuelve_409()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica A");

        // Pre-seed: KC user existente (sin assignment en esta org).
        var foreignerId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(foreignerId, "foreign", "foreign@imed.test");

        var resp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "foreign@imed.test",
            role = "practitioner"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>();
        problem!.Detail.Should().Contain("otra organización");
    }

    [Fact]
    public async Task POST_invitations_email_falla_persiste_outbox_pending()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica Email Down");

        // Forzamos que executeActionsEmail falle 1 vez.
        fixture.Keycloak.EnableExecuteActionsEmailFailing(count: 1);

        var resp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "later@imed.test",
            role = "practitioner"
        });

        // Aún con email fallido, la invitation se crea con flag emailDispatched=false.
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<CreateInvitationResponseDto>();
        body!.EmailDispatched.Should().BeFalse();

        // Outbox event invitation.email-pending registrado.
        var ev = await fixture.FindOutboxByTypeAsync(InvitationEmailPendingEvent.OutboxEventType);
        ev.Should().NotBeNull();

        // Worker eventualmente reintenta y completa (KC ya volvió a OK).
        await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync(InvitationEmailPendingEvent.OutboxEventType),
            predicate: e => e?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(20),
            description: "outbox invitation.email-pending dispatched");

        fixture.Keycloak.ExecuteActionsEmailCallCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DELETE_invitations_marca_revoked_y_disable_kc_user()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica Revoke");

        var inviteResp = await adminClient.PostAsJsonAsync("/api/invitations", new
        {
            email = "todelete@imed.test",
            role = "practitioner"
        });
        var body = await inviteResp.Content.ReadFromJsonAsync<CreateInvitationResponseDto>();
        var invitationId = body!.InvitationId;
        var kcUserId = body.KeycloakUserId;

        var revokeResp = await adminClient.DeleteAsync($"/api/invitations/{invitationId}");

        revokeResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fixture.Keycloak.IsUserEnabled(kcUserId).Should().BeFalse(
            "KC user debe quedar disabled tras revoke como defensa adicional");

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var inv = await db.Invitations.SingleAsync(x => x.Id == invitationId);
        inv.Status.Should().Be(InvitationStatus.Revoked);
    }

    [Fact]
    public async Task GET_invitations_lista_pendings_de_la_org()
    {
        var (_, _, adminClient, _) = await BootstrapOrgAsync("Clínica Lista");

        await adminClient.PostAsJsonAsync("/api/invitations", new { email = "a@imed.test", role = "practitioner" });
        await adminClient.PostAsJsonAsync("/api/invitations", new { email = "b@imed.test", role = "auxiliar" });

        var listResp = await adminClient.GetAsync("/api/invitations");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await listResp.Content.ReadFromJsonAsync<InvitationListItem[]>();
        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        items.Select(i => i.Email).Should().BeEquivalentTo(["a@imed.test", "b@imed.test"]);
    }

    // -------------------------------------------------------------------------
    // Helpers

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

        // Token de org_admin con organization_id + role org_admin.
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

    private sealed record ProblemPayload(string Title, string Detail);
}
