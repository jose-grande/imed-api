namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Objetivo de Turnaround Time por prueba o panel y prioridad (Épica LIS-1).
///
/// <para>FHIR no modela TAT objetivo (solo timestamps). El LIS lo compara contra
/// el TAT real calculado desde la recepción (LIS-DEC-06) para SLA en el dashboard
/// (LIS-8). Ej: hemograma routine = 60 min, glucosa urgente = 30 min.</para>
/// </summary>
public sealed class TatTarget
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    public TatScope Scope { get; private set; }

    /// <summary>Código de la prueba o panel al que aplica. Ej: "CBC", "GLU_FAST".</summary>
    public string TargetCode { get; private set; } = string.Empty;

    public LabRequestPriority Priority { get; private set; } = LabRequestPriority.Routine;

    /// <summary>Minutos objetivo desde recepción hasta liberación.</summary>
    public int Minutes { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private TatTarget() { }

    public static TatTarget Create(
        TatScope scope,
        string targetCode,
        LabRequestPriority priority,
        int minutes,
        Guid? organizationId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(targetCode))
        {
            throw new ArgumentException("TargetCode es obligatorio.", nameof(targetCode));
        }
        if (minutes < 1)
        {
            throw new ArgumentException("Minutes debe ser >= 1.", nameof(minutes));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new TatTarget
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Scope = scope,
            TargetCode = targetCode.Trim(),
            Priority = priority,
            Minutes = minutes,
            CreatedAt = now,
        };
    }
}
