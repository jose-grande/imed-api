using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Invitations;

public sealed class RevokeInvitationHandler(
    IInvitationsRepository invitationsRepo,
    IKeycloakAdminClient keycloakAdmin,
    IInvitationsPersistence persistence,
    IAppAuditLog auditLog,
    ILogger<RevokeInvitationHandler> logger,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(RevokeInvitationCommand command, CancellationToken cancellationToken = default)
    {
        var invitation = await invitationsRepo.GetByIdAsync(command.InvitationId, cancellationToken)
            ?? throw new InvitationConflictException(
                $"Invitation {command.InvitationId} no existe.",
                code: "not_found");

        if (invitation.OrganizationId != command.OrganizationId)
        {
            // Aislamiento cross-tenant: jamás devolver "no encontrado" o "ok",
            // sino tratar como not_found para no filtrar existencia entre orgs.
            throw new InvitationConflictException(
                $"Invitation {command.InvitationId} no existe.",
                code: "not_found");
        }

        if (invitation.Status != InvitationStatus.Pending)
        {
            throw new InvitationConflictException(
                $"No se puede revocar una invitación en estado {invitation.Status}.",
                code: "invalid_status");
        }

        // Revoke en BD primero. Si KC falla en disable user, el lazy provision
        // todavía bloqueará al user (cuando intente loguear, no encontrará
        // invitation pending y no recibirá Practitioner+PractitionerRole; pero
        // su user KC sigue habilitado). La defensa principal es la BD.
        invitation.Revoke(command.RevokedBy, timeProvider);
        await persistence.UpdateAsync(invitation, cancellationToken);

        if (invitation.KeycloakUserId is { } kcUserId)
        {
            try
            {
                await keycloakAdmin.SetUserEnabledAsync(kcUserId, enabled: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Revoke: KC user {UserId} no se pudo deshabilitar; invitation revoked en BD.",
                    kcUserId);
            }
        }

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "invitations",
            action: "revoked",
            severity: AuditSeverity.Info,
            actorUserId: command.RevokedBy,
            organizationId: command.OrganizationId,
            target: $"Invitation:{invitation.Id}",
            payloadJson: JsonSerializer.Serialize(new { invitation.Email }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "Invitation revocada {InvitationId} email={Email} org={OrgId}",
            invitation.Id, invitation.Email, command.OrganizationId);
    }
}
