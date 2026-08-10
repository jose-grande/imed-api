namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Tipo de tubo/contenedor de sangre (LIS-RD-16). Subtipo del tipo de muestra
/// "sangre" con color de tapa, aditivo y volumen mínimo — lo que la recepción
/// imprime/verifica. Global-vs-org como el resto del catálogo (L1-D1).
/// </summary>
public sealed class TubeType
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "EDTA", "SST", "CIT", "HEP".</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Color de la tapa. Ej: "lila", "dorado", "celeste", "verde".</summary>
    public string? Color { get; private set; }

    /// <summary>Aditivo. Ej: "EDTA", "gel separador (SST)", "citrato de sodio", "heparina".</summary>
    public string? Additive { get; private set; }

    /// <summary>Volumen mínimo de llenado en mL. Opcional.</summary>
    public decimal? VolumeMinMl { get; private set; }

    /// <summary>Capacidad nominal del tubo en mL (volumen máximo de llenado). La usa el
    /// motor de plan de colección (R6-F2) para decidir cuántos tubos del mismo aditivo
    /// se necesitan cuando la suma de consumos supera la capacidad.</summary>
    public decimal? CapacityMl { get; private set; }

    /// <summary>Volumen muerto/no recuperable en mL (residual que no se puede aspirar).
    /// El volumen útil del tubo = CapacityMl − DeadVolumeMl.</summary>
    public decimal? DeadVolumeMl { get; private set; }

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TubeType() { }

    public static TubeType Create(
        string code,
        string name,
        string? color = null,
        string? additive = null,
        decimal? volumeMinMl = null,
        decimal? capacityMl = null,
        decimal? deadVolumeMl = null,
        Guid? organizationId = null,
        bool active = true,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new TubeType
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim(),
            Additive = string.IsNullOrWhiteSpace(additive) ? null : additive.Trim(),
            VolumeMinMl = volumeMinMl,
            CapacityMl = capacityMl,
            DeadVolumeMl = deadVolumeMl,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(
        string name,
        string? color,
        string? additive,
        decimal? volumeMinMl,
        decimal? capacityMl = null,
        decimal? deadVolumeMl = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        Color = string.IsNullOrWhiteSpace(color) ? null : color.Trim();
        Additive = string.IsNullOrWhiteSpace(additive) ? null : additive.Trim();
        VolumeMinMl = volumeMinMl;
        CapacityMl = capacityMl;
        DeadVolumeMl = deadVolumeMl;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
