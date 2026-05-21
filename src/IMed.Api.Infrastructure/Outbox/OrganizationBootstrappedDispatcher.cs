using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Notifications;
using IMed.Api.Application.Outbox;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha eventos <c>organization.bootstrapped</c>: re-aplica la sincronización
/// con Keycloak (atributos + rol) cuando el hot-path del bootstrap falló o
/// cuando el evento queda pendiente por cualquier otra razón.
///
/// <para>Idempotente:</para>
/// <list type="bullet">
///   <item><see cref="IKeycloakAdminClient.SetUserAttributesAsync"/> hace GET+merge+PUT.</item>
///   <item><see cref="IKeycloakAdminClient.AssignRealmRoleAsync"/> verifica si el rol
///         ya está asignado antes de POST (no-op si sí).</item>
///   <item>El propio outbox asegura que un dispatch exitoso marca el evento como
///         dispatched y nunca se re-procesa.</item>
/// </list>
///
/// <para>Y la publicación al bus de notificaciones (SSE) avisa al cliente que
/// debe refrescar su token.</para>
/// </summary>
public sealed class OrganizationBootstrappedDispatcher(
    IKeycloakAdminClient keycloakAdmin,
    IUserNotificationBus notificationBus,
    ILogger<OrganizationBootstrappedDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => BootstrapOrganizationPersistence.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        BootstrapOrganizationPersistence.OrganizationBootstrappedPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<BootstrapOrganizationPersistence.OrganizationBootstrappedPayload>(payloadJson);
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

        // Sync Keycloak (idempotente).
        await keycloakAdmin.SetUserAttributesAsync(
            payload.KeycloakUserId,
            new Dictionary<string, string[]>
            {
                ["organization_id"] = [payload.OrganizationId.ToString()],
                ["subscription_tier"] = [payload.Tier]
            },
            cancellationToken);

        await keycloakAdmin.AssignRealmRoleAsync(
            payload.KeycloakUserId,
            payload.RoleName,
            cancellationToken);

        // Notificar al cliente vía SSE: tu JWT está stale, refrescalo.
        // Si nadie está suscrito, el bus descarta el evento — sin error.
        await notificationBus.PublishAsync(
            payload.KeycloakUserId,
            new UserNotification(
                Event: "refresh_token",
                Reason: "organization_assigned",
                Data: new Dictionary<string, string>
                {
                    ["organization_id"] = payload.OrganizationId.ToString()
                }),
            cancellationToken);

        logger.LogInformation(
            "Outbox dispatch OK: organization.bootstrapped user={UserId} org={OrgId}",
            payload.KeycloakUserId, payload.OrganizationId);
    }
}
