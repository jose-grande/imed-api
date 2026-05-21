using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using IMed.Api.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Notifications;

/// <summary>
/// Implementación in-memory del bus por usuario. Cada subscriber abre un
/// <see cref="Channel{T}"/> bounded; las publicaciones se hacen TryWrite con
/// drop-on-full (preferimos perder un evento antes que bloquear al productor).
///
/// <para>Soporta <b>múltiples suscripciones por usuario</b> (varias pestañas).
/// El publish hace fan-out a todos los channels asociados al userId.</para>
///
/// <para><b>Limitación crítica</b>: solo funciona dentro de un proceso. Multi-
/// instance requiere Redis pub/sub o similar (ADR-006 §SSE Hito 2+).</para>
/// </summary>
public sealed class InMemoryUserNotificationBus(ILogger<InMemoryUserNotificationBus> logger)
    : IUserNotificationBus
{
    // Capacity 64: holgura suficiente para ráfagas (cambios de tier
    // back-to-back, eventos de outbox dispatch). Si se llena, dropeamos
    // el más viejo — el cliente que está atrasado pierde notificaciones,
    // pero la siguiente que reciba ya refleja el estado actual.
    private const int ChannelCapacity = 64;

    private readonly ConcurrentDictionary<Guid, ConcurrentBag<Channel<UserNotification>>> _subscribers = new();

    public Task PublishAsync(
        Guid keycloakUserId,
        UserNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (!_subscribers.TryGetValue(keycloakUserId, out var channels))
        {
            // Sin suscriptores: drop silencioso. Es el comportamiento esperado:
            // si el usuario no tiene tabs abiertas, los eventos en vuelo se
            // pierden; al volver, llamará a /api/me y leerá estado fresco.
            return Task.CompletedTask;
        }

        // Fan-out: write a cada channel sin bloquear. Si está lleno, drop.
        var dispatched = 0;
        foreach (var ch in channels)
        {
            if (ch.Writer.TryWrite(notification))
            {
                dispatched++;
            }
            else
            {
                logger.LogWarning(
                    "Channel lleno para user={UserId}; evento '{Event}' descartado.",
                    keycloakUserId, notification.Event);
            }
        }

        logger.LogDebug(
            "Notificación '{Event}' enviada a {Count}/{Total} suscriptor(es) de user={UserId}",
            notification.Event, dispatched, channels.Count, keycloakUserId);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<UserNotification> SubscribeAsync(
        Guid keycloakUserId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<UserNotification>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var bag = _subscribers.GetOrAdd(keycloakUserId, _ => []);
        bag.Add(channel);
        logger.LogDebug("Suscripción nueva para user={UserId}; total={Count}", keycloakUserId, bag.Count);

        try
        {
            await foreach (var notification in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return notification;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
            // ConcurrentBag no soporta remoción aleatoria; reconstruimos sin
            // este channel. Es O(N) pero N=tabs-abiertas-por-usuario.
            if (_subscribers.TryGetValue(keycloakUserId, out var current))
            {
                var remaining = new ConcurrentBag<Channel<UserNotification>>(
                    current.Where(c => c != channel));
                if (remaining.IsEmpty)
                {
                    _subscribers.TryRemove(keycloakUserId, out _);
                }
                else
                {
                    _subscribers[keycloakUserId] = remaining;
                }
            }
        }
    }
}
