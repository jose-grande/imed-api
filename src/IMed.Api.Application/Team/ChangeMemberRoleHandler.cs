using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record ChangeMemberRoleCommand(
    Guid OrganizationId,
    Guid TargetKeycloakUserId,
    Guid ActorKeycloakUserId,
    InvitationRole NewRole);

public sealed record ChangeMemberRoleResult(
    Guid TargetKeycloakUserId,
    string OldRole,
    string NewRole,
    bool PendingKeycloakSync);

/// <summary>
/// Excepción del flow de cambio de rol. El endpoint la mapea a 404/409.
/// </summary>
public sealed class ChangeMemberRoleException(string detail, string code) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>
/// Cambia el rol realm de un miembro del equipo. Reglas:
/// <list type="bullet">
///   <item>El target debe pertenecer a la org del actor (chequea
///   <c>UserPractitionerAssignment</c>; si no hay, 404 — defensa cross-tenant).</item>
///   <item>Si <see cref="ChangeMemberRoleCommand.ActorKeycloakUserId"/> intenta
///   degradarse a sí mismo Y es el único <c>org_admin</c> de la org → 409
///   <c>last_admin</c>. Evita lock-out.</item>
/// </list>
///
/// <para>Outbox como red de seguridad: el hot-path intenta KC remove+add. Si
/// falla, el evento <c>team.role-changed</c> queda pendiente y el worker reintenta.
/// La SSE notification para refresh del JWT del target se publica al finalizar
/// el hot-path OK o por el dispatcher en retry exitoso.</para>
/// </summary>
public sealed class ChangeMemberRoleHandler(
    IUserPractitionerAssignmentRepository assignmentRepo,
    IKeycloakAdminClient keycloakAdmin,
    IChangeMemberRolePersistence persistence,
    IUserNotificationBus notificationBus,
    IAppAuditLog auditLog,
    ILogger<ChangeMemberRoleHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<ChangeMemberRoleResult> HandleAsync(
        ChangeMemberRoleCommand command,
        CancellationToken cancellationToken = default)
    {
        // 1. Pertenencia a la org. Sin assignment, no podemos cambiarle el role.
        var targetAssignment = await assignmentRepo.GetAsync(
            command.TargetKeycloakUserId, command.OrganizationId, cancellationToken);
        if (targetAssignment is null)
        {
            throw new ChangeMemberRoleException(
                $"El miembro {command.TargetKeycloakUserId} no pertenece a esta organización.",
                code: "not_found");
        }

        // 2. Resolver role actual del target en KC. Tomamos el primer role IMed
        // (org_admin > practitioner > auxiliar) que tenga — sirve como "old role"
        // para el remove + audit.
        var currentRoles = await keycloakAdmin.GetUserRealmRolesAsync(
            command.TargetKeycloakUserId, cancellationToken);
        var oldRoleName = ResolveImedRole(currentRoles);
        var newRoleName = InvitationRoleNames.ToRealmRole(command.NewRole);

        if (string.Equals(oldRoleName, newRoleName, StringComparison.Ordinal))
        {
            // Sin cambio efectivo. Devolvemos el estado actual sin tocar nada.
            // Idempotente desde la perspectiva del caller.
            logger.LogDebug(
                "ChangeMemberRole: target {Target} ya tiene role {Role}; no-op.",
                command.TargetKeycloakUserId, newRoleName);
            return new ChangeMemberRoleResult(
                command.TargetKeycloakUserId, oldRoleName ?? "(none)", newRoleName, PendingKeycloakSync: false);
        }

        // 3. Lock-out check: si el actor es el target Y se está degradando del
        // único org_admin de la org → 409.
        if (oldRoleName == InvitationRoleNames.OrgAdmin
            && newRoleName != InvitationRoleNames.OrgAdmin
            && command.ActorKeycloakUserId == command.TargetKeycloakUserId)
        {
            var orgAdminCount = await CountOrgAdminsAsync(command.OrganizationId, cancellationToken);
            if (orgAdminCount <= 1)
            {
                throw new ChangeMemberRoleException(
                    "No te podés degradar: sos el único administrador de la organización. " +
                    "Antes asigná el rol a otro miembro.",
                    code: "last_admin");
            }
        }

        // 4. Persistir outbox event + audit (transacción local) ANTES de tocar KC.
        //    Si KC falla, el worker reproduce idempotentemente.
        var payload = JsonSerializer.Serialize(new TeamRoleChangedEvent.Payload(
            OrganizationId: command.OrganizationId,
            TargetKeycloakUserId: command.TargetKeycloakUserId,
            ActorKeycloakUserId: command.ActorKeycloakUserId,
            OldRole: oldRoleName,
            NewRole: newRoleName));

        var auditEntry = AppAuditLogEntry.Create(
            category: "team",
            action: "role_changed",
            severity: AuditSeverity.Info,
            actorUserId: command.ActorKeycloakUserId,
            organizationId: command.OrganizationId,
            target: $"User:{command.TargetKeycloakUserId}",
            payloadJson: payload,
            timeProvider: timeProvider);

        var outboxEventId = await persistence.RecordRoleChangeAsync(
            payload, auditEntry, command.TargetKeycloakUserId, command.OrganizationId, cancellationToken);

        // 5. Hot-path: KC remove old (si había) + add new. Si falla, outbox cubre.
        try
        {
            if (!string.IsNullOrEmpty(oldRoleName) && IsImedRole(oldRoleName))
            {
                await keycloakAdmin.RemoveRealmRoleAsync(
                    command.TargetKeycloakUserId, oldRoleName, cancellationToken);
            }
            await keycloakAdmin.AssignRealmRoleAsync(
                command.TargetKeycloakUserId, newRoleName, cancellationToken);

            await persistence.MarkOutboxDispatchedAsync(outboxEventId, cancellationToken);

            // SSE: avisar al target que su JWT está stale.
            await notificationBus.PublishAsync(
                command.TargetKeycloakUserId,
                new UserNotification(
                    Event: "refresh_token",
                    Reason: "role_changed",
                    Data: new Dictionary<string, string>
                    {
                        ["new_role"] = newRoleName
                    }),
                cancellationToken);

            await auditLog.RecordAsync(auditEntry, cancellationToken);

            logger.LogInformation(
                "Role change OK target={Target} {OldRole}→{NewRole} actor={Actor}",
                command.TargetKeycloakUserId, oldRoleName, newRoleName, command.ActorKeycloakUserId);

            return new ChangeMemberRoleResult(
                command.TargetKeycloakUserId, oldRoleName ?? "(none)", newRoleName, PendingKeycloakSync: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Role change KC sync falló; outbox pendiente target={Target} {OldRole}→{NewRole}",
                command.TargetKeycloakUserId, oldRoleName, newRoleName);
            return new ChangeMemberRoleResult(
                command.TargetKeycloakUserId, oldRoleName ?? "(none)", newRoleName, PendingKeycloakSync: true);
        }
    }

    private async Task<int> CountOrgAdminsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        // Cruzar usuarios con role org_admin (KC) ∩ assignments de la org (BD).
        // Necesitamos ambos: KC sabe los roles, BD sabe la pertenencia a la org.
        var allOrgAdmins = await keycloakAdmin.GetUsersWithRealmRoleAsync(
            InvitationRoleNames.OrgAdmin, cancellationToken);
        if (allOrgAdmins.Count == 0)
        {
            return 0;
        }

        var count = 0;
        foreach (var userId in allOrgAdmins)
        {
            var assignment = await assignmentRepo.GetAsync(userId, organizationId, cancellationToken);
            if (assignment is not null)
            {
                count++;
            }
        }
        return count;
    }

    private static string? ResolveImedRole(IReadOnlyList<string> roles)
    {
        // Prioridad: si el user tiene varios roles IMed (caso patológico),
        // el más permisivo gana — refleja el ResolveDisplayRole de ListTeamHandler.
        if (roles.Contains(InvitationRoleNames.OrgAdmin))
        {
            return InvitationRoleNames.OrgAdmin;
        }
        if (roles.Contains(InvitationRoleNames.Practitioner))
        {
            return InvitationRoleNames.Practitioner;
        }
        if (roles.Contains(InvitationRoleNames.Auxiliar))
        {
            return InvitationRoleNames.Auxiliar;
        }
        return null;
    }

    private static bool IsImedRole(string roleName) =>
        roleName == InvitationRoleNames.OrgAdmin
        || roleName == InvitationRoleNames.Practitioner
        || roleName == InvitationRoleNames.Auxiliar;
}

/// <summary>
/// Persistencia atómica del role change: outbox event + (opcionalmente) row de
/// metadata. Mantiene el hot-path simple — el handler delega writes acá y
/// concentra la transacción local.
/// </summary>
public interface IChangeMemberRolePersistence
{
    Task<Guid> RecordRoleChangeAsync(
        string payloadJson,
        AppAuditLogEntry auditEntry,
        Guid targetKeycloakUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Outbox event que el dispatcher reproduce cuando el hot-path falló: re-aplica
/// remove old + add new role en KC + dispara SSE refresh_token al target.
/// </summary>
public static class TeamRoleChangedEvent
{
    public const string OutboxEventType = "team.role-changed";

    public sealed record Payload(
        Guid OrganizationId,
        Guid TargetKeycloakUserId,
        Guid ActorKeycloakUserId,
        string? OldRole,
        string NewRole);
}
