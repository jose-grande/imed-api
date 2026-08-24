namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Oferta de una prueba por sede/organización (Épica LIS-1, L1-D2; LIS-DEC-13).
///
/// <para>Fuente de verdad del enrutamiento: ¿esta org realiza la prueba in-house?
/// Default = ofrecida; esta tabla guarda solo las <b>desviaciones</b>
/// (<see cref="Offered"/> = false ⇒ la prueba se referirá a lab externo / send-out).
/// Ausencia de fila = ofrecida.</para>
///
/// <para>PK compuesta (OrganizationId, LabTestId).</para>
/// </summary>
public sealed class LabTestOffering
{
    public Guid OrganizationId { get; private set; }
    public Guid LabTestId { get; private set; }
    public bool Offered { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabTestOffering() { }

    public static LabTestOffering Create(
        Guid organizationId,
        Guid labTestId,
        bool offered,
        TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (labTestId == Guid.Empty)
        {
            throw new ArgumentException("LabTestId es obligatorio.", nameof(labTestId));
        }
        return new LabTestOffering
        {
            OrganizationId = organizationId,
            LabTestId = labTestId,
            Offered = offered,
            UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
        };
    }

    public void SetOffered(bool offered, TimeProvider? timeProvider = null)
    {
        Offered = offered;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
