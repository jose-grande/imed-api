using IMed.Api.Application.Invitations;
using IMed.Api.Application.Practitioners;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.Outbox;
using IMed.Api.Domain.Practitioners;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación de <see cref="IInvitationLazyProvisionPersistence"/>.
/// Toda mutación va dentro de una transacción EF para que el estado quede
/// consistente: assignment + invitation accepted + outbox + audit.
/// </summary>
public sealed class InvitationLazyProvisionPersistence(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<InvitationLazyProvisionPersistence> logger) : IInvitationLazyProvisionPersistence
{
    public async Task UpdateInvitationAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        dbContext.Invitations.Update(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Guid> CompleteLazyProvisionAsync(
        Invitation invitation,
        UserPractitionerAssignment assignment,
        string profileUpdatedPayloadJson,
        AppAuditLogEntry auditEntry,
        CancellationToken cancellationToken = default)
    {
        // Si ya hay transacción ambiente (caso retry desde el OutboxProcessor que
        // mantiene `FOR UPDATE SKIP LOCKED`), reusarla — abrir una nueva tira
        // "connection is already in a transaction". Si no hay (caso primer GET
        // del invitee), abrimos tx local para mantener atomicidad.
        var existingTx = dbContext.Database.CurrentTransaction;
        var ownTx = existingTx is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        try
        {
            dbContext.UserPractitionerAssignments.Add(assignment);
            // El rol recién creado todavía no tiene sedes: se las escribe la sincronización (FAC-9).
            PractitionerLocationsOutbox.Enqueue(dbContext, assignment.OrganizationId, assignment.KeycloakUserId);
            dbContext.Invitations.Update(invitation);

            var outboxEvent = OutboxEvent.Create(
                aggregate: $"User:{assignment.KeycloakUserId}:Org:{assignment.OrganizationId}",
                type: PractitionerProfileUpdatedEvent.OutboxEventType,
                payloadJson: profileUpdatedPayloadJson,
                timeProvider: timeProvider);
            dbContext.OutboxEvents.Add(outboxEvent);

            dbContext.AppAuditLog.Add(auditEntry);

            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownTx is not null)
            {
                await ownTx.CommitAsync(cancellationToken);
            }

            logger.LogInformation(
                "Lazy provision persistido. invitation={InvitationId} user={UserId} practitioner={PractitionerId} outbox={OutboxId}",
                invitation.Id, assignment.KeycloakUserId, assignment.PractitionerFhirId, outboxEvent.Id);

            return outboxEvent.Id;
        }
        catch
        {
            if (ownTx is not null)
            {
                await ownTx.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (ownTx is not null)
            {
                await ownTx.DisposeAsync();
            }
        }
    }

    public async Task RecordLazyProvisionPendingAsync(
        Guid invitationId,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        var aggregate = $"Invitation:{invitationId}";

        // Idempotencia: no acumular outbox events si el GET se reintenta varias
        // veces antes que el worker arranque.
        var existing = await dbContext.OutboxEvents
            .FirstOrDefaultAsync(
                x => x.Type == InvitationLazyProvisionPendingEvent.OutboxEventType
                  && x.Aggregate == aggregate
                  && x.DispatchedAt == null,
                cancellationToken);
        if (existing is not null)
        {
            logger.LogDebug(
                "Outbox lazy-provision-pending ya existe para invitation {InvitationId}; skip.",
                invitationId);
            return;
        }

        var ev = OutboxEvent.Create(
            aggregate: aggregate,
            type: InvitationLazyProvisionPendingEvent.OutboxEventType,
            payloadJson: payloadJson,
            timeProvider: timeProvider);
        dbContext.OutboxEvents.Add(ev);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Outbox lazy-provision-pending creado invitation={InvitationId} ev={EventId}",
            invitationId, ev.Id);
    }

    public async Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default)
    {
        var ev = await dbContext.OutboxEvents
            .FirstOrDefaultAsync(x => x.Id == outboxEventId, cancellationToken);
        if (ev is null || ev.DispatchedAt is not null)
        {
            return;
        }
        ev.MarkDispatched(timeProvider);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
