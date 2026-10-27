using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Authorization;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record SetMemberFunctionsCommand(
    Guid OrganizationId,
    Guid TargetKeycloakUserId,
    Guid ActorKeycloakUserId,
    IReadOnlyList<string> Functions);

public sealed record SetMemberFunctionsResult(
    Guid TargetKeycloakUserId,
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    bool PendingKeycloakSync);

/// <summary>
/// Reemplaza las funciones de un miembro del equipo (plan de salida a producción, A1).
///
/// <para><b>Reemplaza, no suma.</b> La pantalla manda la lista completa que el administrador ve
/// marcada; calcular la diferencia acá evita que dos administradores editando a la vez dejen una
/// mezcla que ninguno de los dos eligió. Solo toca roles de <see cref="FunctionalRoles"/>: el rol
/// principal se cambia por su propio camino, que protege al último administrador.</para>
///
/// <para>Mismo patrón que <see cref="ChangeMemberRoleHandler"/>: outbox primero, Keycloak en el
/// hot-path y el worker reintenta si Keycloak falla.</para>
/// </summary>
public sealed class SetMemberFunctionsHandler(
    IUserPractitionerAssignmentRepository assignmentRepo,
    IKeycloakAdminClient keycloakAdmin,
    IChangeMemberRolePersistence persistence,
    IUserNotificationBus notificationBus,
    IAppAuditLog auditLog,
    ILogger<SetMemberFunctionsHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<SetMemberFunctionsResult> HandleAsync(
        SetMemberFunctionsCommand command, CancellationToken cancellationToken = default)
    {
        var deseadas = FunctionalRoles.Normalize(command.Functions);

        if (await assignmentRepo.GetAsync(command.TargetKeycloakUserId, command.OrganizationId, cancellationToken) is null)
        {
            throw new ChangeMemberRoleException(
                $"El miembro {command.TargetKeycloakUserId} no pertenece a esta organización.",
                code: "not_found");
        }

        var actuales = (await keycloakAdmin.GetUserRealmRolesAsync(command.TargetKeycloakUserId, cancellationToken))
            .Where(FunctionalRoles.IsFunctional)
            .ToList();
        var agregar = deseadas.Except(actuales).ToList();
        var quitar = actuales.Except(deseadas).ToList();

        if (agregar.Count == 0 && quitar.Count == 0)
        {
            return new SetMemberFunctionsResult(command.TargetKeycloakUserId, deseadas, [], [], PendingKeycloakSync: false);
        }

        var payload = JsonSerializer.Serialize(new TeamFunctionsChangedEvent.Payload(
            command.OrganizationId, command.TargetKeycloakUserId, command.ActorKeycloakUserId, agregar, quitar));
        var audit = AppAuditLogEntry.Create(
            category: "team",
            action: "functions_changed",
            severity: AuditSeverity.Info,
            actorUserId: command.ActorKeycloakUserId,
            organizationId: command.OrganizationId,
            target: $"User:{command.TargetKeycloakUserId}",
            payloadJson: payload,
            timeProvider: timeProvider);

        var eventoId = await persistence.RecordOutboxAsync(
            TeamFunctionsChangedEvent.OutboxEventType, payload,
            command.TargetKeycloakUserId, command.OrganizationId, cancellationToken);

        try
        {
            await TeamFunctionsChangedEvent.ApplyAsync(
                keycloakAdmin, notificationBus, command.TargetKeycloakUserId, agregar, quitar, cancellationToken);
            await persistence.MarkOutboxDispatchedAsync(eventoId, cancellationToken);
            await auditLog.RecordAsync(audit, cancellationToken);

            logger.LogInformation(
                "Funciones de {Target}: +[{Added}] -[{Removed}] por {Actor}",
                command.TargetKeycloakUserId, string.Join(",", agregar), string.Join(",", quitar),
                command.ActorKeycloakUserId);
            return new SetMemberFunctionsResult(command.TargetKeycloakUserId, deseadas, agregar, quitar, PendingKeycloakSync: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Cambio de funciones de {Target}: Keycloak falló, queda en outbox.", command.TargetKeycloakUserId);
            return new SetMemberFunctionsResult(command.TargetKeycloakUserId, deseadas, agregar, quitar, PendingKeycloakSync: true);
        }
    }
}

/// <summary>Evento outbox del cambio de funciones; el dispatcher reaplica lo mismo que el hot-path.</summary>
public static class TeamFunctionsChangedEvent
{
    public const string OutboxEventType = "team.functions-changed";

    public sealed record Payload(
        Guid OrganizationId,
        Guid TargetKeycloakUserId,
        Guid ActorKeycloakUserId,
        IReadOnlyList<string> Add,
        IReadOnlyList<string> Remove);

    /// <summary>
    /// Aplica el cambio en Keycloak y avisa al usuario que refresque su token. Idempotente: asignar
    /// o quitar un rol que ya está en ese estado es no-op en el cliente de Keycloak.
    /// </summary>
    public static async Task ApplyAsync(
        IKeycloakAdminClient keycloakAdmin,
        IUserNotificationBus notificationBus,
        Guid target,
        IReadOnlyList<string> add,
        IReadOnlyList<string> remove,
        CancellationToken ct)
    {
        // Solo funciones: un payload alterado no puede quitar ni dar un rol principal.
        foreach (var rol in remove.Where(FunctionalRoles.IsFunctional))
        {
            await keycloakAdmin.RemoveRealmRoleAsync(target, rol, ct);
        }
        foreach (var rol in add.Where(FunctionalRoles.IsFunctional))
        {
            await keycloakAdmin.AssignRealmRoleAsync(target, rol, ct);
        }
        await notificationBus.PublishAsync(
            target,
            new UserNotification(Event: "refresh_token", Reason: "functions_changed", Data: new Dictionary<string, string>()),
            ct);
    }
}
