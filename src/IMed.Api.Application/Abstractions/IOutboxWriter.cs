namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Encola un evento en el outbox desde la capa de aplicación, cuando no hay otra escritura en la
/// base con la cual hacerlo viajar (por ejemplo, después de escribir en HAPI).
/// </summary>
public interface IOutboxWriter
{
    Task EnqueueAsync(string eventType, string aggregate, string payloadJson, CancellationToken ct = default);
}
