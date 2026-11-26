using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record SetMemberSeesPatientsCommand(
    Guid OrganizationId,
    Guid TargetKeycloakUserId,
    Guid ActorKeycloakUserId,
    bool SeesPatients);

public sealed record SetMemberSeesPatientsResult(Guid TargetKeycloakUserId, bool SeesPatients);

/// <summary>
/// Marca si un administrador atiende pacientes (Mi equipo). Decide si aparece en el selector de
/// médico de la agenda y si se le pide completar su perfil profesional.
///
/// <para><b>Solo administradores.</b> El médico atiende siempre y el resto del personal nunca
/// recibe citas; para ellos la marca no significaría nada, y aceptarla haría creer que cambia
/// algo.</para>
/// </summary>
public sealed class SetMemberSeesPatientsHandler(
    IOrganizationMemberRepository memberRepo,
    IKeycloakAdminClient keycloakAdmin,
    IAppAuditLog auditLog,
    ILogger<SetMemberSeesPatientsHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<SetMemberSeesPatientsResult> HandleAsync(
        SetMemberSeesPatientsCommand command, CancellationToken cancellationToken = default)
    {
        var member = await memberRepo.GetAsync(command.TargetKeycloakUserId, command.OrganizationId, cancellationToken)
            ?? throw new ChangeMemberRoleException(
                $"El miembro {command.TargetKeycloakUserId} no pertenece a esta organización.",
                code: "not_found");

        var roles = await keycloakAdmin.GetUserRealmRolesAsync(command.TargetKeycloakUserId, cancellationToken);
        if (roles.Contains(InvitationRoleNames.Practitioner, StringComparer.Ordinal))
        {
            throw new ChangeMemberRoleException(
                "Un médico siempre atiende pacientes: la marca es solo para administradores.",
                code: "not_applicable");
        }
        if (!roles.Contains(InvitationRoleNames.OrgAdmin, StringComparer.Ordinal))
        {
            throw new ChangeMemberRoleException(
                "Solo un administrador puede marcarse como que atiende pacientes. " +
                "Si esta persona es médico, cambiale el rol a Médico.",
                code: "not_applicable");
        }

        member.SetSeesPatients(command.SeesPatients, timeProvider);
        await memberRepo.UpdateAsync(member, cancellationToken);

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "team",
            action: "sees_patients_changed",
            severity: AuditSeverity.Info,
            actorUserId: command.ActorKeycloakUserId,
            organizationId: command.OrganizationId,
            target: $"User:{command.TargetKeycloakUserId}",
            payloadJson: JsonSerializer.Serialize(new { seesPatients = command.SeesPatients }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "Atiende pacientes de {Target}: {Value} por {Actor}",
            command.TargetKeycloakUserId, command.SeesPatients, command.ActorKeycloakUserId);
        return new SetMemberSeesPatientsResult(command.TargetKeycloakUserId, command.SeesPatients);
    }
}
