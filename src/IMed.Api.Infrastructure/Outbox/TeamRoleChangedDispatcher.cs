using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Outbox;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha eventos <c>team.role-changed</c>: re-aplica remove old role + add
/// new role en KC + dispara SSE refresh_token al target. Idempotente:
/// <see cref="IKeycloakAdminClient.RemoveRealmRoleAsync"/> y
/// <see cref="IKeycloakAdminClient.AssignRealmRoleAsync"/> ambos chequean estado
/// previo y son no-op si ya está en el target state.
/// </summary>
public sealed class TeamRoleChangedDispatcher(
    IKeycloakAdminClient keycloakAdmin,
    IUserNotificationBus notificationBus,
    ILogger<TeamRoleChangedDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => TeamRoleChangedEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        TeamRoleChangedEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TeamRoleChangedEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException(
                $"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' es null.");
        }

        if (!string.IsNullOrEmpty(payload.OldRole) && IsImedRole(payload.OldRole))
        {
            await keycloakAdmin.RemoveRealmRoleAsync(
                payload.TargetKeycloakUserId, payload.OldRole, cancellationToken);
        }
        await keycloakAdmin.AssignRealmRoleAsync(
            payload.TargetKeycloakUserId, payload.NewRole, cancellationToken);

        await notificationBus.PublishAsync(
            payload.TargetKeycloakUserId,
            new UserNotification(
                Event: "refresh_token",
                Reason: "role_changed",
                Data: new Dictionary<string, string>
                {
                    ["new_role"] = payload.NewRole
                }),
            cancellationToken);

        logger.LogInformation(
            "Outbox dispatch OK: team.role-changed target={Target} {OldRole}→{NewRole}",
            payload.TargetKeycloakUserId, payload.OldRole, payload.NewRole);
    }

    private static bool IsImedRole(string roleName) =>
        roleName == InvitationRoleNames.OrgAdmin
        || roleName == InvitationRoleNames.Practitioner
        || roleName == InvitationRoleNames.Auxiliar;
}
