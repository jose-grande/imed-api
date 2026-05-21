using System.Text.Json;
using IMed.Api.Application.Practitioners;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Outbox;
using IMed.Api.Domain.Practitioners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación del puerto <see cref="IUpsertPractitionerProfilePersistence"/>.
/// Persiste el assignment + outbox event + audit log en una sola transacción
/// (mismo patrón que <see cref="BootstrapOrganizationPersistence"/>).
/// </summary>
public sealed class UpsertPractitionerProfilePersistence(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<UpsertPractitionerProfilePersistence> logger) : IUpsertPractitionerProfilePersistence
{
    public async Task<Guid> UpsertAsync(
        UpsertPractitionerProfilePersistenceInput input,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 1. Insert o update del assignment.
            UserPractitionerAssignment assignment;
            if (input.IsCreate)
            {
                assignment = UserPractitionerAssignment.Create(
                    keycloakUserId: input.KeycloakUserId,
                    organizationId: input.OrganizationId,
                    practitionerFhirId: input.PractitionerFhirId,
                    practitionerRoleFhirId: input.PractitionerRoleFhirId,
                    timeProvider: timeProvider);
                dbContext.UserPractitionerAssignments.Add(assignment);
            }
            else
            {
                // Cargar tracked y actualizar PractitionerRoleFhirId si cambió.
                assignment = await dbContext.UserPractitionerAssignments
                    .FirstAsync(
                        x => x.KeycloakUserId == input.KeycloakUserId
                          && x.OrganizationId == input.OrganizationId,
                        cancellationToken);

                if (!string.IsNullOrEmpty(input.PractitionerRoleFhirId)
                    && assignment.PractitionerRoleFhirId != input.PractitionerRoleFhirId)
                {
                    assignment.UpdatePractitionerRole(input.PractitionerRoleFhirId, timeProvider);
                }
            }

            // 2. Outbox event con payload mínimo para que el dispatcher reproduzca
            //    la sync con KC (idempotente).
            var outboxPayload = JsonSerializer.Serialize(new PractitionerProfileUpdatedEvent.Payload(
                KeycloakUserId: input.KeycloakUserId,
                OrganizationId: input.OrganizationId,
                PractitionerFhirId: input.PractitionerFhirId));

            var outboxEvent = OutboxEvent.Create(
                aggregate: $"User:{input.KeycloakUserId}:Org:{input.OrganizationId}",
                type: PractitionerProfileUpdatedEvent.OutboxEventType,
                payloadJson: outboxPayload,
                timeProvider: timeProvider);
            dbContext.OutboxEvents.Add(outboxEvent);

            // 3. Audit log.
            var auditEntry = AppAuditLogEntry.Create(
                category: "practitioner",
                action: input.IsCreate ? "profile_created" : "profile_updated",
                severity: AuditSeverity.Info,
                actorUserId: input.KeycloakUserId,
                organizationId: input.OrganizationId,
                target: $"Practitioner:{input.PractitionerFhirId}",
                payloadJson: outboxPayload,
                timeProvider: timeProvider);
            dbContext.AppAuditLog.Add(auditEntry);

            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Practitioner profile persistido. user={UserId} practitioner={PractitionerId} create={IsCreate} outboxEventId={OutboxId}",
                input.KeycloakUserId, input.PractitionerFhirId, input.IsCreate, outboxEvent.Id);

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
        var ev = await dbContext.OutboxEvents
            .FirstOrDefaultAsync(x => x.Id == outboxEventId, cancellationToken);

        if (ev is null)
        {
            logger.LogWarning("Outbox event {Id} no encontrado al marcarlo dispatched.", outboxEventId);
            return;
        }
        if (ev.DispatchedAt is not null)
        {
            return;
        }

        ev.MarkDispatched(timeProvider);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
