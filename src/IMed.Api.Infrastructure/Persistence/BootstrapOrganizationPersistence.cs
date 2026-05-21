using System.Text.Json;
using IMed.Api.Application.Organizations;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Outbox;
using IMed.Api.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación del puerto <see cref="IBootstrapOrganizationPersistence"/>.
/// Ejecuta todo el bootstrap dentro de una sola transacción EF y agrega un
/// evento al outbox para reconciliación futura (ADR-006 §Outbox).
/// </summary>
public sealed class BootstrapOrganizationPersistence(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<BootstrapOrganizationPersistence> logger) : IBootstrapOrganizationPersistence
{
    public const string OutboxEventType = "organization.bootstrapped";

    public async Task<Guid> BootstrapAsync(
        BootstrapPersistenceInput input,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var subscription = OrganizationSubscription.CreateBootstrapTrial(
                organizationId: input.OrganizationId,
                name: input.OrganizationName,
                fhirId: input.OrganizationFhirId,
                timeProvider: timeProvider);
            dbContext.OrganizationSubscriptions.Add(subscription);

            var assignment = UserTierAssignment.Create(
                keycloakUserId: input.KeycloakUserId,
                organizationId: input.OrganizationId,
                tier: input.Tier,
                assignedBy: input.AssignedBy,
                timeProvider: timeProvider);
            dbContext.UserTierAssignments.Add(assignment);

            var outboxPayload = JsonSerializer.Serialize(new OrganizationBootstrappedPayload(
                OrganizationId: input.OrganizationId,
                OrganizationFhirId: input.OrganizationFhirId,
                KeycloakUserId: input.KeycloakUserId,
                Tier: input.Tier.ToClaimValue(),
                RoleName: input.RoleName,
                AssignedBy: input.AssignedBy,
                CreatedAt: input.CreatedAt));

            var outboxEvent = OutboxEvent.Create(
                aggregate: $"Organization:{input.OrganizationId}",
                type: OutboxEventType,
                payloadJson: outboxPayload,
                timeProvider: timeProvider);
            dbContext.OutboxEvents.Add(outboxEvent);

            // Audit log de "bootstrap iniciado" — visible al admin desde la
            // consulta antes incluso de que la sync KC complete.
            var auditEntry = AppAuditLogEntry.Create(
                category: "organization",
                action: "bootstrap_started",
                severity: AuditSeverity.Info,
                actorUserId: input.KeycloakUserId,
                organizationId: input.OrganizationId,
                target: $"Organization:{input.OrganizationId}",
                payloadJson: outboxPayload,
                timeProvider: timeProvider);
            dbContext.AppAuditLog.Add(auditEntry);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Bootstrap persistido. org={OrgId}, user={UserId}, outboxEventId={OutboxId}",
                input.OrganizationId, input.KeycloakUserId, outboxEvent.Id);

            return outboxEvent.Id;
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default)
    {
        // No-throw si el evento no existe (race con el worker que ya lo procesó):
        // el efecto final es el mismo (dispatched), no hay nada que reportar.
        var ev = await dbContext.OutboxEvents
            .FirstOrDefaultAsync(x => x.Id == outboxEventId, cancellationToken);

        if (ev is null)
        {
            logger.LogWarning("Outbox event {Id} no encontrado al marcarlo dispatched.", outboxEventId);
            return;
        }

        if (ev.DispatchedAt is not null)
        {
            return; // ya estaba dispatched (worker se adelantó); idempotente.
        }

        ev.MarkDispatched(timeProvider);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Payload del evento <c>organization.bootstrapped</c>. El worker del outbox
    /// lo deserializa y reproduce la sincronización con Keycloak.
    /// </summary>
    public sealed record OrganizationBootstrappedPayload(
        Guid OrganizationId,
        string OrganizationFhirId,
        Guid KeycloakUserId,
        string Tier,
        string RoleName,
        Guid AssignedBy,
        DateTimeOffset CreatedAt);
}
