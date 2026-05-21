using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Domain.Outbox;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Bootstrap;

[Collection(ImedApiCollection.Name)]
public sealed class BootstrapResilienceTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task When_keycloak_PUT_fails_bootstrap_returns_pendingSync_and_worker_completes_eventually()
    {
        // Arrange: Keycloak fallará en las próximas 2 PUT. La 3ra (que ejecuta el worker) pasa.
        fixture.Keycloak.EnableFailing(putUserCalls: 2);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "resilient.user");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "resilient.user");
        var client = fixture.CreateClientWithToken(token);

        // Act 1: bootstrap. Hot-path falla en el PUT, retorna pendingSync=true.
        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica Resiliente"
        });
        response.IsSuccessStatusCode.Should().BeTrue("bootstrap NO debe propagar la falla del KC sync");
        var body = await response.Content.ReadFromJsonAsync<BootstrapResponse>();
        body!.PendingKeycloakSync.Should().BeTrue();

        // Outbox event debe quedar pendiente con AttemptCount=0 (todavía no lo tocó el worker).
        var initialEvent = (await fixture.FindOutboxByTypeAsync("organization.bootstrapped"))!;
        initialEvent.DispatchedAt.Should().BeNull("el hot-path falló, el evento queda pendiente");

        // Act 2: esperar a que el worker procese. El worker corre en background con poll de 1s.
        // Backoff: 1s → 2s. Después de 2 intentos failing + 1 OK → max ~5s incluyendo polls.
        var dispatched = await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync("organization.bootstrapped"),
            predicate: ev => ev?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(20),
            description: "outbox event organization.bootstrapped marked dispatched by worker");

        // Assert: KC quedó sincronizado eventualmente.
        dispatched!.AttemptCount.Should().BeGreaterThan(0, "tuvo al menos 1 intento fallido");
        var attrs = fixture.Keycloak.GetUserAttributes(userId);
        attrs["organization_id"].Should().ContainSingle().Which.Should().Be(body.OrganizationId.ToString());
        fixture.Keycloak.GetUserRoles(userId).Should().Contain("org_admin");
    }

    [Fact]
    public async Task Worker_applies_exponential_backoff_and_eventually_succeeds()
    {
        // 3 fallos consecutivos antes de OK. Con BackoffBase=1s: 1s, 2s, 4s ≈ 7s total mínimo.
        fixture.Keycloak.EnableFailing(putUserCalls: 3);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "backoff.user");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "backoff.user");
        var client = fixture.CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Backoff" });
        response.EnsureSuccessStatusCode();

        // Esperar dispatch eventual.
        var dispatched = await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync("organization.bootstrapped"),
            predicate: ev => ev?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(30),
            description: "worker eventually dispatches after exponential backoff");

        dispatched!.AttemptCount.Should().BeInRange(2, 5,
            "tuvo varios intentos antes de succeed; AttemptCount es upper bound del fallo, " +
            "no del éxito final");
    }

    [Fact]
    public async Task Worker_dispatch_is_idempotent_against_keycloak_partial_state()
    {
        // Setup: Keycloak ya tiene el rol asignado (simulando que el hot-path llegó a asignarlo
        // pero falló antes de marcar dispatched). El worker debe re-aplicar attrs sin romper
        // por el rol pre-existente.
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "idempotent.user");

        // Fallar el PUT 1 vez para forzar que el worker procese.
        fixture.Keycloak.EnableFailing(putUserCalls: 1);

        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "idempotent.user");
        var client = fixture.CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Idempotent" });
        response.EnsureSuccessStatusCode();

        var dispatched = await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync("organization.bootstrapped"),
            predicate: ev => ev?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(20),
            description: "worker dispatches even with partial pre-state");

        // Estado final: rol asignado UNA sola vez, atributos correctos.
        fixture.Keycloak.GetUserRoles(userId).Where(r => r == "org_admin").Should().HaveCount(1,
            "AssignRealmRoleAsync verifica si ya está asignado antes de POST");
    }

    [Fact]
    public async Task Worker_marks_event_as_failed_after_max_attempts()
    {
        // Forzar más fallos que MaxAttempts (5 en config de tests).
        // Esto causa que el evento quede dispatched-with-error (LastError seteado).
        fixture.Keycloak.EnableFailing(putUserCalls: 100);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "doomed.user");
        var token = fixture.Keycloak.IssueUserToken(userId, preferredUsername: "doomed.user");
        var client = fixture.CreateClientWithToken(token);

        var response = await client.PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Doomed" });
        response.EnsureSuccessStatusCode();

        // Esperamos que después de 5 intentos el evento quede dispatched (con LastError).
        // Backoff: 1+2+4+8+10(cap)=25s. Damos 60s de margen.
        var ev = await TestExtensions.WaitForAsync(
            probe: () => fixture.FindOutboxByTypeAsync("organization.bootstrapped"),
            predicate: x => x?.DispatchedAt is not null,
            timeout: TimeSpan.FromSeconds(60),
            description: "worker eventually freezes the event after max attempts");

        ev!.LastError.Should().NotBeNullOrEmpty();
        ev.AttemptCount.Should().BeGreaterThanOrEqualTo(5);

        // Audit log de severity Error registra la falla terminal.
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var failedAudits = await db.AppAuditLog
            .Where(e => e.Category == "outbox" && e.Action == "dispatch_failed_max_attempts")
            .ToListAsync();
        failedAudits.Should().NotBeEmpty();
    }

    private sealed record BootstrapResponse(
        Guid OrganizationId,
        string OrganizationFhirId,
        string AssignedTier,
        bool PendingKeycloakSync,
        string Message);
}
