using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record SetMemberActiveCommand(
    Guid OrganizationId, Guid TargetKeycloakUserId, Guid ActorKeycloakUserId, bool Active);

public sealed record SetMemberActiveResult(Guid KeycloakUserId, bool IsActive, bool PendingKeycloakSync);

/// <summary>
/// Da de baja o reactiva a un miembro (<c>PUT /api/team/{id}/active</c>, solo administradores).
///
/// <para>La baja se hace desde el portal y no desde la consola de Keycloak: así la membresía local
/// y Keycloak quedan de acuerdo. Dar de baja marca al miembro inactivo (el registro de sesión lo
/// rechaza) y deshabilita la cuenta en Keycloak (no puede volver a iniciar sesión ni renovar el
/// token). Su historia —sedes, perfil clínico, lo que firmó— se conserva.</para>
///
/// <para>Nadie se da de baja a sí mismo, y no se puede dar de baja al último administrador activo:
/// la organización quedaría sin quien la administre.</para>
/// </summary>
public sealed class SetMemberActiveHandler(
    IOrganizationMemberRepository members,
    IKeycloakAdminClient keycloak,
    IAppAuditLog auditLog,
    ILogger<SetMemberActiveHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<SetMemberActiveResult> HandleAsync(SetMemberActiveCommand cmd, CancellationToken ct = default)
    {
        var member = await members.GetAsync(cmd.TargetKeycloakUserId, cmd.OrganizationId, ct)
            ?? throw new ChangeMemberRoleException(
                $"El miembro {cmd.TargetKeycloakUserId} no pertenece a esta organización.", code: "not_found");

        if (member.IsActive == cmd.Active)
        {
            return new SetMemberActiveResult(member.KeycloakUserId, member.IsActive, PendingKeycloakSync: false);
        }

        if (!cmd.Active)
        {
            if (cmd.TargetKeycloakUserId == cmd.ActorKeycloakUserId)
            {
                throw new ChangeMemberRoleException("No podés darte de baja a vos mismo.", code: "self");
            }
            var roles = await keycloak.GetUserRealmRolesAsync(cmd.TargetKeycloakUserId, ct);
            if (roles.Contains(InvitationRoleNames.OrgAdmin, StringComparer.Ordinal)
                && await CountActiveAdminsAsync(cmd.OrganizationId, ct) <= 1)
            {
                throw new ChangeMemberRoleException(
                    "Es el único administrador activo de la organización. Antes asigná el rol a otro miembro.",
                    code: "last_admin");
            }
            member.Deactivate(timeProvider);
        }
        else
        {
            member.Reactivate(timeProvider);
        }
        await members.UpdateAsync(member, ct);

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "team",
            action: cmd.Active ? "member_reactivated" : "member_deactivated",
            severity: AuditSeverity.Info,
            actorUserId: cmd.ActorKeycloakUserId,
            organizationId: cmd.OrganizationId,
            target: $"User:{cmd.TargetKeycloakUserId}",
            payloadJson: JsonSerializer.Serialize(new { cmd.Active }),
            timeProvider: timeProvider), ct);

        // La membresía ya cambió: si Keycloak falla, el registro de sesión igual rechaza al miembro
        // inactivo. Se informa para que el administrador sepa que la cuenta sigue habilitada allá.
        var pending = false;
        try
        {
            await keycloak.SetUserEnabledAsync(cmd.TargetKeycloakUserId, cmd.Active, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pending = true;
            logger.LogWarning(ex, "Baja/alta de {UserId}: Keycloak no respondió; la cuenta quedó sin sincronizar",
                cmd.TargetKeycloakUserId);
        }

        return new SetMemberActiveResult(member.KeycloakUserId, member.IsActive, pending);
    }

    private async Task<int> CountActiveAdminsAsync(Guid organizationId, CancellationToken ct)
    {
        var admins = await keycloak.GetUsersWithRealmRoleAsync(InvitationRoleNames.OrgAdmin, ct);
        var count = 0;
        foreach (var userId in admins)
        {
            if (await members.GetAsync(userId, organizationId, ct) is { IsActive: true })
            {
                count++;
            }
        }
        return count;
    }
}
