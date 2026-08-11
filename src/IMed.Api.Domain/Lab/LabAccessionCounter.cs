namespace IMed.Api.Domain.Lab;

/// <summary>
/// Contador diario de accession por organización (Épica LIS-3, L3-D5). Genera la
/// parte secuencial del accession number (`{ORG3}-{yyyyMMdd}-{seq}`). El
/// incremento atómico lo maneja el repositorio (upsert) para evitar duplicados
/// bajo concurrencia.
///
/// <para>PK compuesta (OrganizationId, LocationId, Day): el correlativo es <b>por sede</b>
/// (ADR-008). Con una sola secuencia por organización, dos sucursales intercalan sus números y
/// cada una ve su numeración con huecos — que en un laboratorio se lee como muestras perdidas.</para>
/// </summary>
public sealed class LabAccessionCounter
{
    public Guid OrganizationId { get; private set; }
    public Guid LocationId { get; private set; }
    public DateOnly Day { get; private set; }
    public int Seq { get; private set; }

    private LabAccessionCounter() { }

    public static LabAccessionCounter Create(Guid organizationId, Guid locationId, DateOnly day, int seq = 0)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        return new LabAccessionCounter
        {
            OrganizationId = organizationId,
            LocationId = locationId,
            Day = day,
            Seq = seq,
        };
    }
}
