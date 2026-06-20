namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Una versión de un <see cref="LabPanel"/> (Épica LIS-1, L1-D4). La membresía
/// de pruebas vive en <see cref="LabPanelVersionItem"/>, atada a la versión —
/// así un reporte histórico conserva la composición vigente al momento de la orden.
/// </summary>
public sealed class LabPanelVersion
{
    public Guid Id { get; private set; }
    public Guid LabPanelId { get; private set; }
    public int Version { get; private set; }
    public DateOnly EffectiveDate { get; private set; }
    public PanelVersionStatus Status { get; private set; } = PanelVersionStatus.Draft;
    public DateTimeOffset CreatedAt { get; private set; }

    private LabPanelVersion() { }

    public static LabPanelVersion Create(
        Guid labPanelId,
        int version,
        DateOnly effectiveDate,
        PanelVersionStatus status = PanelVersionStatus.Draft,
        TimeProvider? timeProvider = null)
    {
        if (labPanelId == Guid.Empty)
        {
            throw new ArgumentException("LabPanelId es obligatorio.", nameof(labPanelId));
        }
        if (version < 1)
        {
            throw new ArgumentException("Version debe ser >= 1.", nameof(version));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabPanelVersion
        {
            Id = Guid.CreateVersion7(now),
            LabPanelId = labPanelId,
            Version = version,
            EffectiveDate = effectiveDate,
            Status = status,
            CreatedAt = now,
        };
    }

    public void SetStatus(PanelVersionStatus status) => Status = status;
}

/// <summary>
/// Pertenencia de una prueba a una versión de panel. Tabla puente con orden de
/// visualización. PK compuesta (LabPanelVersionId, LabTestId).
/// </summary>
public sealed class LabPanelVersionItem
{
    public Guid LabPanelVersionId { get; private set; }
    public Guid LabTestId { get; private set; }
    public int DisplayOrder { get; private set; }

    private LabPanelVersionItem() { }

    public static LabPanelVersionItem Create(Guid labPanelVersionId, Guid labTestId, int displayOrder)
    {
        if (labPanelVersionId == Guid.Empty)
        {
            throw new ArgumentException("LabPanelVersionId es obligatorio.", nameof(labPanelVersionId));
        }
        if (labTestId == Guid.Empty)
        {
            throw new ArgumentException("LabTestId es obligatorio.", nameof(labTestId));
        }
        return new LabPanelVersionItem
        {
            LabPanelVersionId = labPanelVersionId,
            LabTestId = labTestId,
            DisplayOrder = displayOrder,
        };
    }
}
