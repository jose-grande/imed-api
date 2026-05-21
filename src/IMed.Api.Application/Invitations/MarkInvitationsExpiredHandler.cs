using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Invitations;

/// <summary>
/// Cleanup manual: marca como <c>expired</c> todas las invitations <c>pending</c>
/// creadas antes de <see cref="MarkInvitationsExpiredCommand.Threshold"/>. El
/// caller (Org Admin) decide el threshold — típicamente "todo lo más viejo que
/// el lifespan del action token KC".
///
/// <para>Cron formal en Épica I (Quartz). Por ahora, manual.</para>
/// </summary>
public sealed class MarkInvitationsExpiredHandler(
    IInvitationsRepository invitationsRepo,
    IInvitationsPersistence persistence,
    IAppAuditLog auditLog,
    ILogger<MarkInvitationsExpiredHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<int> HandleAsync(
        MarkInvitationsExpiredCommand command,
        Guid actorUserId,
        CancellationToken cancellationToken = default)
    {
        var candidates = await invitationsRepo.ListPendingCreatedBeforeAsync(
            command.OrganizationId, command.Threshold, cancellationToken);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var marked = 0;
        foreach (var invitation in candidates)
        {
            invitation.MarkExpired(timeProvider);
            await persistence.UpdateAsync(invitation, cancellationToken);
            marked++;
        }

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "invitations",
            action: "expired_batch",
            severity: AuditSeverity.Info,
            actorUserId: actorUserId,
            organizationId: command.OrganizationId,
            target: $"Organization:{command.OrganizationId}",
            payloadJson: JsonSerializer.Serialize(new
            {
                count = marked,
                threshold = command.Threshold
            }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "Expired batch marcado: {Count} invitations en org {OrgId}, threshold={Threshold:O}",
            marked, command.OrganizationId, command.Threshold);

        return marked;
    }
}
