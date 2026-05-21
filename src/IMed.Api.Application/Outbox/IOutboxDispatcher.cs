namespace IMed.Api.Application.Outbox;

/// <summary>
/// Despacha un tipo concreto de evento del outbox. Una implementación por
/// <c>OutboxEvent.Type</c>; el <c>OutboxProcessor</c> resuelve la implementación
/// correcta vía DI keyed.
///
/// <para>Contrato:</para>
/// <list type="bullet">
///   <item><b>Idempotente</b>: un retry tras fallo parcial NO debe romper.</item>
///   <item><b>No-throw para terminal failures</b>: si el evento es estructuralmente
///         inválido (payload corrupto), lanzar <see cref="OutboxPermanentFailureException"/>
///         para marcar el evento como permanently failed (no más retries).</item>
///   <item><b>Throw para fallas transitorias</b>: el processor las atrapa y aplica
///         backoff exponencial.</item>
/// </list>
/// </summary>
public interface IOutboxDispatcher
{
    /// <summary>El <c>OutboxEvent.Type</c> que despacha esta implementación.</summary>
    string EventType { get; }

    Task DispatchAsync(string payloadJson, CancellationToken cancellationToken);
}

/// <summary>
/// Falla permanente del despacho. El processor marca el evento como
/// dispatched-with-error (no reintenta) y registra audit log de severity error.
/// </summary>
public sealed class OutboxPermanentFailureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
