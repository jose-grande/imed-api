namespace IMed.Api.Domain.Lab;

/// <summary>
/// Devolución de un informe al técnico desde la estación de Validación.
///
/// <para><b>Por qué existe:</b> la pantalla de validación solo sabía liberar. Sus únicos
/// desenlaces eran "Validar y liberar" o "Cancelar", y <i>cancelar no registraba nada</i>: si el
/// bioanalista veía un resultado que no cerraba, se iba de la pantalla y el sistema no guardaba
/// memoria de eso. El siguiente que entraba veía el mismo pendiente, sin ninguna nota. Una
/// estación cuyos únicos desenlaces son aprobar o irse no puede hacer su trabajo — y menos
/// cuando lo que le llega es justamente lo que el motor de reglas decidió no auto-validar.</para>
///
/// <para><b>Ciclo:</b> nace abierta (<see cref="ResolvedAt"/> null) y el informe sale de la cola
/// de validación; el técnico lo ve devuelto en su worklist con el motivo. Se cierra sola cuando
/// el técnico corrige algún resultado del informe, que es lo que lo devuelve a la cola.</para>
///
/// <para>Append-only en la práctica: una devolución no se edita ni se borra, solo se resuelve.</para>
/// </summary>
public sealed class ResultValidationRejection
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Informe devuelto.</summary>
    public string ReportFhirId { get; private set; } = string.Empty;

    /// <summary>Orden de origen: es por donde el técnico la reencuentra.</summary>
    public string? ServiceRequestFhirId { get; private set; }

    public Guid ReasonId { get; private set; }

    /// <summary>Detalle libre del validador. Complementa al motivo codificado, no lo reemplaza.</summary>
    public string? Note { get; private set; }

    public Guid RejectedBy { get; private set; }
    public DateTimeOffset RejectedAt { get; private set; }

    /// <summary>Null mientras la devolución siga abierta.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    private ResultValidationRejection() { }

    public static ResultValidationRejection Create(
        Guid organizationId,
        string reportFhirId,
        string? serviceRequestFhirId,
        Guid reasonId,
        string? note,
        Guid rejectedBy,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(reportFhirId))
        {
            throw new ArgumentException("El informe es obligatorio.", nameof(reportFhirId));
        }

        return new ResultValidationRejection
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ReportFhirId = reportFhirId,
            ServiceRequestFhirId = serviceRequestFhirId,
            ReasonId = reasonId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RejectedBy = rejectedBy,
            RejectedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
        };
    }

    /// <summary>La corrección del técnico cierra la devolución y devuelve el informe a la cola.</summary>
    public void Resolve(TimeProvider? timeProvider = null)
    {
        ResolvedAt ??= (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
