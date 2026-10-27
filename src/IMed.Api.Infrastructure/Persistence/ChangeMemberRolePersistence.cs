using IMed.Api.Application.Team;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class ChangeMemberRolePersistence(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<ChangeMemberRolePersistence> logger) : IChangeMemberRolePersistence
{
    public async Task<Guid> RecordRoleChangeAsync(
        string payloadJson,
        AppAuditLogEntry auditEntry,
        Guid targetKeycloakUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        // Audit log lo emite el handler con IAppAuditLog (separado de la tx); acá solo se
        // persiste el outbox para garantizar el retry path.
        _ = auditEntry;
        return await RecordOutboxAsync(
            TeamRoleChangedEvent.OutboxEventType, payloadJson, targetKeycloakUserId, organizationId, cancellationToken);
    }

    public async Task<Guid> RecordOutboxAsync(
        string eventType,
        string payloadJson,
        Guid targetKeycloakUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var existingTx = dbContext.Database.CurrentTransaction;
        var ownTx = existingTx is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var ev = OutboxEvent.Create(
                aggregate: $"User:{targetKeycloakUserId}:Org:{organizationId}",
                type: eventType,
                payloadJson: payloadJson,
                timeProvider: timeProvider);
            dbContext.OutboxEvents.Add(ev);

            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownTx is not null)
            {
                await ownTx.CommitAsync(cancellationToken);
            }

            logger.LogDebug("{Type} outbox event {Id} persistido", eventType, ev.Id);
            return ev.Id;
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
