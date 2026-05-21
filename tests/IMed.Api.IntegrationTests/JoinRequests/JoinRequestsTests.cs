using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Domain.JoinRequests;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.JoinRequests;

[Collection(ImedApiCollection.Name)]
public sealed class JoinRequestsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task POST_join_requests_happy_path_persists_and_notifies_admin()
    {
        var (orgId, orgFhirId, _, founderId) = await BootstrapOrgAsync("Clínica Anónima");

        var anonClient = fixture.Factory.CreateClient();
        var resp = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "interested@imed.test",
            fullName = "Interesado Médico",
            organizationFhirId = orgFhirId,
            message = "Quiero unirme al equipo"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<SubmitResponseDto>();
        body!.AdminNotified.Should().BeTrue();

        // BD: join request persistida pending.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jr = await db.JoinRequests.SingleAsync();
        jr.Email.Should().Be("interested@imed.test");
        jr.FullName.Should().Be("Interesado Médico");
        jr.OrganizationId.Should().Be(orgId);
        jr.Status.Should().Be(JoinRequestStatus.Pending);

        // Email al founder capturado.
        fixture.Email.Sent.Should().HaveCount(1);
        var msg = fixture.Email.Sent.Single();
        msg.To.Should().Be("founder@imed.test");
        msg.Subject.Should().Contain("Clínica Anónima");
        // El nombre se HTML-encodea — chequear partes ASCII para evitar fragilidad.
        msg.BodyHtml.Should().Contain("Interesado");
        msg.BodyHtml.Should().Contain("interested@imed.test");

        _ = founderId;
    }

    [Fact]
    public async Task POST_join_requests_duplicate_pending_devuelve_409()
    {
        var (_, orgFhirId, _, _) = await BootstrapOrgAsync("Clínica Dup");

        var anonClient = fixture.Factory.CreateClient();
        var first = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "dup@imed.test",
            fullName = "Dup",
            organizationFhirId = orgFhirId
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "dup@imed.test",
            fullName = "Dup Again",
            organizationFhirId = orgFhirId
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task POST_join_requests_org_inexistente_devuelve_404()
    {
        var anonClient = fixture.Factory.CreateClient();
        var resp = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "ghost@imed.test",
            fullName = "Ghost",
            organizationFhirId = "org-fhir-doesnotexist"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Approve_flow_completo_crea_invitation_y_notifica_solicitante()
    {
        var (orgId, orgFhirId, adminClient, _) = await BootstrapOrgAsync("Clínica Approve");

        // Submit anónimo.
        var anonClient = fixture.Factory.CreateClient();
        var submitResp = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "newmember@imed.test",
            fullName = "Nuevo Miembro",
            organizationFhirId = orgFhirId
        });
        submitResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var submitBody = await submitResp.Content.ReadFromJsonAsync<SubmitResponseDto>();
        var joinRequestId = submitBody!.JoinRequestId;

        fixture.Email.Reset(); // empezamos a contar emails post-approve.

        // Admin aprueba.
        var approveResp = await adminClient.PostAsync(
            $"/api/join-requests/{joinRequestId}/approve",
            content: null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var approveBody = await approveResp.Content.ReadFromJsonAsync<ApproveResponseDto>();
        approveBody!.LinkedInvitationId.Should().NotBe(Guid.Empty);
        approveBody.ApplicantNotified.Should().BeTrue();

        // BD: join request approved + invitation pending creada con role=Practitioner.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var jr = await db.JoinRequests.SingleAsync(x => x.Id == joinRequestId);
        jr.Status.Should().Be(JoinRequestStatus.Approved);
        jr.LinkedInvitationId.Should().Be(approveBody.LinkedInvitationId);

        var invitation = await db.Invitations.SingleAsync();
        invitation.Email.Should().Be("newmember@imed.test");
        invitation.OrganizationId.Should().Be(orgId);
        invitation.Role.Should().Be(IMed.Api.Domain.Invitations.InvitationRole.Practitioner);

        // Emails: nuestro "aprobada" al solicitante (KC manda el de action tokens
        // por separado — capturado por fixture.Keycloak.LastExecuteActionsEmail).
        var ourEmail = fixture.Email.Sent.SingleOrDefault(e => e.To == "newmember@imed.test");
        ourEmail.Should().NotBeNull();
        ourEmail!.Subject.Should().Contain("aprobada");

        fixture.Keycloak.ExecuteActionsEmailCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Approve_request_already_rejected_devuelve_409()
    {
        var (_, orgFhirId, adminClient, _) = await BootstrapOrgAsync("Clínica RejectFirst");

        var anonClient = fixture.Factory.CreateClient();
        var submitResp = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "x@imed.test",
            fullName = "Equis",
            organizationFhirId = orgFhirId
        });
        var submitBody = await submitResp.Content.ReadFromJsonAsync<SubmitResponseDto>();
        var jrId = submitBody!.JoinRequestId;

        // Reject primero.
        var rejectResp = await adminClient.PostAsJsonAsync(
            $"/api/join-requests/{jrId}/reject", new { reason = "no thanks" });
        rejectResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Approve después → 409.
        var approveResp = await adminClient.PostAsync(
            $"/api/join-requests/{jrId}/approve", content: null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reject_request_already_approved_devuelve_409()
    {
        var (_, orgFhirId, adminClient, _) = await BootstrapOrgAsync("Clínica ApproveFirst");

        var anonClient = fixture.Factory.CreateClient();
        var submitResp = await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "y@imed.test",
            fullName = "Yyy",
            organizationFhirId = orgFhirId
        });
        var submitBody = await submitResp.Content.ReadFromJsonAsync<SubmitResponseDto>();
        var jrId = submitBody!.JoinRequestId;

        var approveResp = await adminClient.PostAsync(
            $"/api/join-requests/{jrId}/approve", content: null);
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Reject sobre approved → 409.
        var rejectResp = await adminClient.PostAsJsonAsync(
            $"/api/join-requests/{jrId}/reject", new { reason = "tarde" });
        rejectResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task GET_join_requests_org_admin_lista_pendings()
    {
        var (_, orgFhirId, adminClient, _) = await BootstrapOrgAsync("Clínica Lista JR");

        var anonClient = fixture.Factory.CreateClient();
        await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "p1@imed.test",
            fullName = "P1",
            organizationFhirId = orgFhirId
        });
        await anonClient.PostAsJsonAsync("/api/join-requests", new
        {
            email = "p2@imed.test",
            fullName = "P2",
            organizationFhirId = orgFhirId
        });

        var listResp = await adminClient.GetAsync("/api/join-requests");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await listResp.Content.ReadFromJsonAsync<JoinRequestListItemDto[]>();
        items.Should().NotBeNull();
        items!.Should().HaveCount(2);
        items.Select(i => i.Email).Should().BeEquivalentTo(["p1@imed.test", "p2@imed.test"]);
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

        var orgFhirId = fixture.Hapi.Organizations.Single(o => o.Partition == $"org-{body!.OrganizationId}").FhirId;

        var adminToken = fixture.Keycloak.IssueUserToken(
            founderId,
            organizationId: body!.OrganizationId.ToString(),
            preferredUsername: "founder",
            realmRoles: ["org_admin"]);
        var adminClient = fixture.CreateClientWithToken(adminToken);
        return (body.OrganizationId, orgFhirId, adminClient, founderId);
    }

    private sealed record BootstrapResponseDto(Guid OrganizationId, string AssignedTier, bool PendingKeycloakSync);
    private sealed record SubmitResponseDto(Guid JoinRequestId, bool AdminNotified);
    private sealed record ApproveResponseDto(Guid JoinRequestId, Guid LinkedInvitationId, bool ApplicantNotified);
    private sealed record JoinRequestListItemDto(Guid Id, string Email, string FullName, string? Message, DateTimeOffset CreatedAt);
}
