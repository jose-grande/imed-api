namespace IMed.Api.Domain.Lab;

/// <summary>
/// Contador diario de número de orden por organización (F1, número correlativo
/// legible). Genera la parte secuencial del número de orden
/// (`ORD-{yyyyMMdd}-{seq}`). El incremento atómico lo maneja el repositorio
/// (upsert) para evitar duplicados bajo concurrencia. PK compuesta
/// (OrganizationId, Day). Separado del accession (que es de la muestra) para que
/// los correlativos no se mezclen.
/// </summary>
public sealed class LabOrderCounter
{
    public Guid OrganizationId { get; private set; }
    public DateOnly Day { get; private set; }
    public int Seq { get; private set; }

    private LabOrderCounter() { }

    public static LabOrderCounter Create(Guid organizationId, DateOnly day, int seq = 0)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        return new LabOrderCounter
        {
            OrganizationId = organizationId,
            Day = day,
            Seq = seq,
        };
    }
}
