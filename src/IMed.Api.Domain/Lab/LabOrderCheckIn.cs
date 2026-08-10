namespace IMed.Api.Domain.Lab;

/// <summary>
/// Marca de "ingreso" de una orden de laboratorio (F2): el paciente solicitó
/// hacerse los exámenes EN la clínica, así que la orden entra a la cola de
/// recepción. Sin esta marca, una orden creada por el médico no aparece en la
/// cola (el paciente puede llevarla a un laboratorio externo). PK compuesta
/// (OrganizationId, Requisition = número de orden).
/// </summary>
public sealed class LabOrderCheckIn
{
    public Guid OrganizationId { get; private set; }

    /// <summary>Número de orden (requisition) que se ingresa.</summary>
    public string Requisition { get; private set; } = string.Empty;

    public DateTimeOffset CheckedInAt { get; private set; }
    public Guid CheckedInBy { get; private set; }

    private LabOrderCheckIn() { }

    public static LabOrderCheckIn Create(Guid organizationId, string requisition, Guid checkedInBy, TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(requisition))
        {
            throw new ArgumentException("Requisition es obligatoria.", nameof(requisition));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabOrderCheckIn
        {
            OrganizationId = organizationId,
            Requisition = requisition.Trim(),
            CheckedInBy = checkedInBy,
            CheckedInAt = now,
        };
    }
}
