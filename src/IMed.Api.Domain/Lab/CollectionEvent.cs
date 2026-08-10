namespace IMed.Api.Domain.Lab;

/// <summary>Tipo de evento de la cadena de custodia de un contenedor (R6-F3).</summary>
public enum CollectionEventType
{
    LabelGenerated = 0,
    LabelPrinted = 1,
    LabelReprinted = 2,
    CollectionStarted = 3,
    Collected = 4,
    Received = 5,
    Rejected = 6,
    Stored = 7,

    /// <summary>Identificación positiva del paciente (nombre + fecha de nacimiento)
    /// en admisión/toma (R6-F6, decisión §8.6). Evidencia de la cadena de custodia.</summary>
    IdentityVerified = 8,
}

/// <summary>
/// Evento del timeline de cadena de custodia de un <see cref="CollectionPlanContainer"/>
/// (R6-F3). Cada transición de estado o (re)impresión de etiqueta deja un evento con
/// hora, responsable y detalle (motivo de rechazo/reimpresión, ubicación, etc.).
/// </summary>
public sealed class CollectionEvent
{
    public Guid Id { get; private set; }
    public Guid CollectionPlanContainerId { get; private set; }
    public CollectionEventType Type { get; private set; }
    public DateTimeOffset At { get; private set; }

    /// <summary>Usuario responsable. Best-effort.</summary>
    public Guid? By { get; private set; }

    /// <summary>Detalle: motivo de rechazo/reimpresión, ubicación de almacenamiento, etc.</summary>
    public string? Detail { get; private set; }

    private CollectionEvent() { }

    public static CollectionEvent Create(
        Guid collectionPlanContainerId,
        CollectionEventType type,
        Guid? by = null,
        string? detail = null,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CollectionEvent
        {
            Id = Guid.CreateVersion7(now),
            CollectionPlanContainerId = collectionPlanContainerId,
            Type = type,
            At = now,
            By = by,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim(),
        };
    }
}
