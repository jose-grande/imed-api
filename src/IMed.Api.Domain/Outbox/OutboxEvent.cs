namespace IMed.Api.Domain.Outbox;

/// <summary>
/// Outbox pattern (ver ADR-006 §Outbox). Una transacción DB del caso de uso
/// inserta aquí los eventos a propagar (publicación SSE, llamadas a Keycloak
/// Admin API que pueden retriarse, etc). Un worker aparte consume pendientes
/// con backoff.
/// </summary>
public sealed class OutboxEvent
{
    public Guid Id { get; private set; }
    public string Aggregate { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = "{}";
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? LastError { get; private set; }

    private OutboxEvent() { }

    public static OutboxEvent Create(
        string aggregate,
        string type,
        string payloadJson,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OutboxEvent
        {
            Id = Guid.CreateVersion7(now),
            Aggregate = aggregate,
            Type = type,
            Payload = payloadJson,
            CreatedAt = now,
            NextAttemptAt = now
        };
    }

    public void MarkDispatched(TimeProvider? timeProvider = null)
    {
        DispatchedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        LastError = null;
        NextAttemptAt = null;
    }

    /// <summary>
    /// Congela el evento como dispatched <b>preservando</b> <see cref="LastError"/>
    /// y <see cref="AttemptCount"/>. Se usa cuando el procesador decide no
    /// reintentar más (max attempts alcanzado o falla permanente).
    /// </summary>
    public void Freeze(TimeProvider? timeProvider = null)
    {
        DispatchedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        NextAttemptAt = null;
    }

    public void MarkFailed(string error, TimeSpan backoff, TimeProvider? timeProvider = null)
    {
        AttemptCount++;
        LastError = error;
        NextAttemptAt = (timeProvider ?? TimeProvider.System).GetUtcNow().Add(backoff);
    }
}
