namespace IMed.Api.Application.Notifications;

/// <summary>
/// Bus de notificaciones por usuario (Keycloak <c>sub</c>). El SSE endpoint se
/// suscribe en <see cref="SubscribeAsync"/>; los productores (outbox dispatchers,
/// endpoints de cambio de tier, etc.) llaman <see cref="PublishAsync"/>.
///
/// <para>En Hito 1 la implementación es in-memory: no escala a multi-instance.
/// ADR-006 §SSE documenta el upgrade path a Redis pub/sub para Hito 2+.</para>
///
/// <para>Si nadie está suscrito al usuario, <see cref="PublishAsync"/> descarta
/// el evento sin error (fire-and-forget).</para>
/// </summary>
public interface IUserNotificationBus
{
    Task PublishAsync(Guid keycloakUserId, UserNotification notification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve un async-stream de notificaciones para el usuario. Cierra el
    /// stream cuando el <paramref name="cancellationToken"/> se cancela.
    /// </summary>
    IAsyncEnumerable<UserNotification> SubscribeAsync(Guid keycloakUserId, CancellationToken cancellationToken);
}

/// <summary>
/// Notificación enviada al cliente vía SSE. <see cref="Event"/> es el tipo
/// <c>event:</c> de SSE; <see cref="Reason"/> y <see cref="Data"/> viajan en el
/// payload JSON (campo <c>data:</c>).
/// </summary>
public sealed record UserNotification(
    string Event,
    string Reason,
    IReadOnlyDictionary<string, string>? Data = null);
