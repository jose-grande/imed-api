namespace IMed.Api.Domain.Lab;

/// <summary>
/// Contador diario de accession por organización (Épica LIS-3, L3-D5). Genera la
/// parte secuencial del accession number (`{ORG3}-{yyyyMMdd}-{seq}`). El
/// incremento atómico lo maneja el repositorio (upsert) para evitar duplicados
/// bajo concurrencia. PK compuesta (OrganizationId, Day).
/// </summary>
public sealed class LabAccessionCounter
{
    public Guid OrganizationId { get; private set; }
    public DateOnly Day { get; private set; }
    public int Seq { get; private set; }

    private LabAccessionCounter() { }

    public static LabAccessionCounter Create(Guid organizationId, DateOnly day, int seq = 0)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        return new LabAccessionCounter
        {
            OrganizationId = organizationId,
            Day = day,
            Seq = seq,
        };
    }
}
