using IMed.Api.Application.Invitations;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación del puerto <see cref="IInvitationsPersistence"/>. Cada
/// operación corre en su propia transacción para que la mutación de la
/// invitation y su outbox event (cuando aplica) sean atómicos.
/// </summary>
public sealed class InvitationsPersistence(
    AppDbContext dbContext,
    TimeProvider timeProvider,
    ILogger<InvitationsPersistence> logger) : IInvitationsPersistence
{
    public async Task CreatePendingAsync(Invitation invitation, Guid createdBy, CancellationToken cancellationToken = default)
    {
        _ = createdBy;
        // Sólo persiste la invitation. El audit log lo emite el handler vía
        // IAppAuditLog (mismo patrón que UpsertPractitionerProfile). Si en el
        // futuro requerimos atomicidad estricta, mover el audit acá.
        dbContext.Invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Invitation persistida {InvitationId}", invitation.Id);
    }

    public async Task RecordEmailPendingAsync(Guid invitationId, string payloadJson, CancellationToken cancellationToken = default)
    {
        // Idempotencia: si ya hay un email-pending no-dispatched para esta
        // invitation, no insertamos otro. Evita acumular eventos cuando el
        // hot-path falla varias veces antes que el worker arranque.
        var existing = await dbContext.OutboxEvents
            .FirstOrDefaultAsync(
                x => x.Type == InvitationEmailPendingEvent.OutboxEventType
                  && x.Aggregate == AggregateName(invitationId)
                  && x.DispatchedAt == null,
                cancellationToken);

        if (existing is not null)
        {
            logger.LogDebug(
                "Outbox event email-pending ya existe para invitation {InvitationId}; skip.",
                invitationId);
            return;
        }

        var ev = OutboxEvent.Create(
            aggregate: AggregateName(invitationId),
            type: InvitationEmailPendingEvent.OutboxEventType,
            payloadJson: payloadJson,
            timeProvider: timeProvider);
        dbContext.OutboxEvents.Add(ev);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Outbox event email-pending creado para invitation {InvitationId}, ev={EventId}",
            invitationId, ev.Id);
    }

    public async Task UpdateAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        dbContext.Invitations.Update(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string AggregateName(Guid invitationId) => $"Invitation:{invitationId}";
}
