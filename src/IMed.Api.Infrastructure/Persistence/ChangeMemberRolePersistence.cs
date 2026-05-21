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
        var existingTx = dbContext.Database.CurrentTransaction;
        var ownTx = existingTx is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var ev = OutboxEvent.Create(
                aggregate: $"User:{targetKeycloakUserId}:Org:{organizationId}",
                type: TeamRoleChangedEvent.OutboxEventType,
                payloadJson: payloadJson,
                timeProvider: timeProvider);
            dbContext.OutboxEvents.Add(ev);
            // Audit log lo emite el handler con IAppAuditLog (separado de la
            // tx); acá solo persistimos el outbox para garantizar el retry path.
            _ = auditEntry;

            await dbContext.SaveChangesAsync(cancellationToken);
            if (ownTx is not null)
            {
                await ownTx.CommitAsync(cancellationToken);
            }

            logger.LogDebug("team.role-changed outbox event {Id} persistido", ev.Id);
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
