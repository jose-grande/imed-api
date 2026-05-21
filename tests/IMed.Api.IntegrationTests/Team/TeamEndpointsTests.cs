using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Team;

[Collection(ImedApiCollection.Name)]
public sealed class TeamEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GET_api_team_sin_auth_devuelve_401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync("/api/team");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_api_team_sin_organization_id_devuelve_400()
    {
        // User logueado pero todavía sin org (caso recién registrado, antes de bootstrap).
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "newbie", "newbie@imed.test");
        var token = fixture.Keycloak.IssueUserToken(userId);  // sin organizationId
        var client = fixture.CreateClientWithToken(token);

        var response = await client.GetAsync("/api/team");
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GET_api_team_org_sin_miembros_devuelve_array_vacio()
    {
        // Bootstrap una org sin agregar PractitionerRoles después.
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "founder", "founder@imed.test");
        var bootstrapToken = fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "founder");
        var bootstrapClient = fixture.CreateClientWithToken(bootstrapToken);

        var bootstrapResp = await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Empty Clinic",
            nationalTaxId = "0801-1990-99999"
        });
        bootstrapResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await bootstrapResp.Content.ReadFromJsonAsync<BootstrapResponseDto>();
        body.Should().NotBeNull();

        // Token con organization_id ya populated (post-bootstrap).
        var orgToken = fixture.Keycloak.IssueUserToken(founderId,
            organizationId: body!.OrganizationId.ToString());
        var client = fixture.CreateClientWithToken(orgToken);

        var teamResp = await client.GetAsync("/api/team");
        teamResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var team = await teamResp.Content.ReadFromJsonAsync<TeamMember[]>();
        team.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public async Task GET_api_team_devuelve_miembros_seedados()
    {
        // Arrange: bootstrap una org + seed 2 PractitionerRoles + Practitioners + Assignments + KC users.
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "founder", "founder@imed.test");
        var bootstrapToken = fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "founder");
        var bootstrapClient = fixture.CreateClientWithToken(bootstrapToken);

        var bootstrapResp = await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica con Equipo",
            nationalTaxId = "0801-1990-77777"
        });
        bootstrapResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await bootstrapResp.Content.ReadFromJsonAsync<BootstrapResponseDto>();
        var orgId = body!.OrganizationId;
        var orgFhirId = fixture.Hapi.Organizations.Single().FhirId;

        // Seed 2 team members directo (no via flow real — eso es F3+).
        // Practitioner Alice (org_admin), Practitioner Bob (practitioner).
        var aliceUserId = Guid.NewGuid();
        var bobUserId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(aliceUserId, "alice", "alice@imed.test");
        fixture.Keycloak.RegisterUser(bobUserId, "bob", "bob@imed.test");

        // KC: asignar roles realm-level (usamos AssignRealmRoleAsync indirectamente
        // via _users[].Roles set por el mock — pero nuestro endpoint pide via API,
        // así que los roles deben existir cuando GetUserRealmRolesAsync se llame).
        // Workaround: la fixture's mock AssignRealmRoleAsync POST simula la asignación.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var kc = scope.ServiceProvider.GetRequiredService<IMed.Api.Application.Abstractions.IKeycloakAdminClient>();
            await kc.AssignRealmRoleAsync(aliceUserId, "org_admin");
            await kc.AssignRealmRoleAsync(bobUserId, "practitioner");
        }

        // HAPI: registrar 2 practitioners + roles en el mock.
        fixture.Hapi.RegisterTeamMember(
            organizationFhirId: orgFhirId,
            practitionerFhirId: "prac-alice",
            practitionerRoleFhirId: "role-alice",
            family: "Smith",
            given: "Alice",
            specialtyCode: "394814009",
            specialtyDisplay: "Medicina general");
        fixture.Hapi.RegisterTeamMember(
            organizationFhirId: orgFhirId,
            practitionerFhirId: "prac-bob",
            practitionerRoleFhirId: "role-bob",
            family: "Jones",
            given: "Bob",
            specialtyCode: "394537008",
            specialtyDisplay: "Pediatría");

        // BD: insertar 2 UserPractitionerAssignments para que ListTeamHandler hidrate.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(
                keycloakUserId: aliceUserId,
                organizationId: orgId,
                practitionerFhirId: "prac-alice",
                practitionerRoleFhirId: "role-alice"));
            db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(
                keycloakUserId: bobUserId,
                organizationId: orgId,
                practitionerFhirId: "prac-bob",
                practitionerRoleFhirId: "role-bob"));
            await db.SaveChangesAsync();
        }

        // Act: GET /api/team con token de Alice.
        var aliceToken = fixture.Keycloak.IssueUserToken(aliceUserId, organizationId: orgId.ToString());
        var client = fixture.CreateClientWithToken(aliceToken);
        var teamResp = await client.GetAsync("/api/team");

        // Assert.
        teamResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var team = await teamResp.Content.ReadFromJsonAsync<TeamMember[]>();
        team.Should().NotBeNull();
        team!.Should().HaveCount(2);

        var alice = team.SingleOrDefault(m => m.KeycloakUserId == aliceUserId);
        alice.Should().NotBeNull();
        alice!.DisplayName.Should().Be("Alice Smith");
        alice.Role.Should().Be("org_admin", "Alice tiene realm role org_admin asignado en el mock");
        alice.PractitionerRoleFhirId.Should().Be("role-alice");
        alice.SpecialtyDisplay.Should().Be("Medicina general");

        var bob = team.SingleOrDefault(m => m.KeycloakUserId == bobUserId);
        bob.Should().NotBeNull();
        bob!.Role.Should().Be("practitioner");
        bob.SpecialtyDisplay.Should().Be("Pediatría");
    }

    private sealed record BootstrapResponseDto(Guid OrganizationId, string AssignedTier, bool PendingKeycloakSync);
}
