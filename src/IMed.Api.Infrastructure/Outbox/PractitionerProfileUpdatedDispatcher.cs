using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Outbox;
using IMed.Api.Application.Practitioners;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha eventos <c>me.practitioner-profile-updated</c>: re-aplica el set
/// del atributo <c>practitioner_id</c> en Keycloak cuando el hot-path falló
/// y emite un SSE para que el cliente refresque su token.
///
/// <para>Idempotente: <see cref="IKeycloakAdminClient.SetUserAttributesAsync"/>
/// hace GET+merge+PUT, así que reaplicar el mismo valor no rompe.</para>
/// </summary>
public sealed class PractitionerProfileUpdatedDispatcher(
    IKeycloakAdminClient keycloakAdmin,
    IUserNotificationBus notificationBus,
    ILogger<PractitionerProfileUpdatedDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => PractitionerProfileUpdatedEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        PractitionerProfileUpdatedEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PractitionerProfileUpdatedEvent.Payload>(payloadJson);
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

        await keycloakAdmin.SetUserAttributesAsync(
            payload.KeycloakUserId,
            new Dictionary<string, string[]>
            {
                ["practitioner_id"] = [payload.PractitionerFhirId]
            },
            cancellationToken);

        await notificationBus.PublishAsync(
            payload.KeycloakUserId,
            new UserNotification(
                Event: "refresh_token",
                Reason: "practitioner_profile_updated",
                Data: new Dictionary<string, string>
                {
                    ["practitioner_id"] = payload.PractitionerFhirId
                }),
            cancellationToken);

        logger.LogInformation(
            "Outbox dispatch OK: me.practitioner-profile-updated user={UserId} practitioner={PractitionerId}",
            payload.KeycloakUserId, payload.PractitionerFhirId);
    }
}
