using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Outbox;
using IMed.Api.Application.Team;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha <c>team.functions-changed</c> cuando el hot-path no pudo aplicar el cambio de funciones
/// en Keycloak. Reaplica lo mismo, que es idempotente.
/// </summary>
public sealed class TeamFunctionsChangedDispatcher(
    IKeycloakAdminClient keycloakAdmin,
    IUserNotificationBus notificationBus,
    ILogger<TeamFunctionsChangedDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => TeamFunctionsChangedEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        TeamFunctionsChangedEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TeamFunctionsChangedEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' es null.");
        }

        await TeamFunctionsChangedEvent.ApplyAsync(
            keycloakAdmin, notificationBus, payload.TargetKeycloakUserId, payload.Add, payload.Remove,
            cancellationToken);
        logger.LogInformation("Outbox dispatch OK: {Type} target={Target}", EventType, payload.TargetKeycloakUserId);
    }
}
