namespace IMed.Api.Domain.Lab;

/// <summary>
/// Evento del ciclo de vida de una muestra (Épica LIS-3). Espejo operativo de la
/// cadena de custodia (que también se registra como `Provenance` FHIR). Permite
/// reconstruir la trazabilidad: recibida → en proceso → centrifugada → … .
/// </summary>
public sealed class SpecimenEvent
{
    public Guid Id { get; private set; }
    public Guid OrderIntakeId { get; private set; }
    public SpecimenEventType EventType { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? PerformedBy { get; private set; }
    public SpecimenIncident? Incident { get; private set; }
    public string? Notes { get; private set; }

    private SpecimenEvent() { }

    public static SpecimenEvent Create(
        Guid orderIntakeId,
        SpecimenEventType eventType,
        Guid? performedBy = null,
        SpecimenIncident? incident = null,
        string? notes = null,
        TimeProvider? timeProvider = null)
    {
        if (orderIntakeId == Guid.Empty)
        {
            throw new ArgumentException("OrderIntakeId es obligatorio.", nameof(orderIntakeId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new SpecimenEvent
        {
            Id = Guid.CreateVersion7(now),
            OrderIntakeId = orderIntakeId,
            EventType = eventType,
            OccurredAt = now,
            PerformedBy = performedBy,
            Incident = incident,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
    }
}
