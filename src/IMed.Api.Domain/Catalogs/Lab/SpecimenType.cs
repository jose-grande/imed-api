namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Tipo de muestra del catálogo (LIS-RD-26). Generaliza más allá de sangre/orina:
/// semen, saliva, LCR, tejido, hisopado, heces, etc. El tubo de sangre es un
/// subtipo (<see cref="TubeType"/>). Global-vs-org como el resto del catálogo
/// (L1-D1): <c>OrganizationId == null</c> = plantilla global del seed.
///
/// <para>Cada tipo define el <see cref="CollectionMode"/> (la trae vs on-site,
/// LIS-RD-23), el método/sitio por defecto (LIS-RD-27), el recipiente y la
/// ventana de estabilidad (semen/gases = corta) — datos que la recepción y la
/// cola de toma usan para derivar qué recolectar.</para>
/// </summary>
public sealed class SpecimenType
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "BLOOD", "URINE", "SEMEN", "SWAB".</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Código SNOMED CT (o HL7 v2-0487) del tipo de muestra. Opcional.</summary>
    public string? SnomedCode { get; private set; }

    /// <summary>Modo de colección por defecto (la trae vs on-site). LIS-RD-23.</summary>
    public CollectionMode CollectionMode { get; private set; } = CollectionMode.OnSite;

    /// <summary>Método de colección por defecto (venopunción, punción capilar, hisopado…). LIS-RD-27.</summary>
    public string? DefaultMethod { get; private set; }

    /// <summary>Sitio anatómico por defecto (opcional).</summary>
    public string? DefaultBodySite { get; private set; }

    public ContainerKind ContainerKind { get; private set; } = ContainerKind.Tube;

    /// <summary>Ventana de estabilidad en minutos (semen/gases = corta). Null = sin límite definido.</summary>
    public int? StabilityMinutes { get; private set; }

    /// <summary>Disciplina/departamento que la procesa.</summary>
    public LabDepartment Discipline { get; private set; } = LabDepartment.Other;

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SpecimenType() { }

    public static SpecimenType Create(
        string code,
        string name,
        CollectionMode collectionMode = CollectionMode.OnSite,
        string? snomedCode = null,
        string? defaultMethod = null,
        string? defaultBodySite = null,
        ContainerKind containerKind = ContainerKind.Tube,
        int? stabilityMinutes = null,
        LabDepartment discipline = LabDepartment.Other,
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
        return new SpecimenType
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            SnomedCode = string.IsNullOrWhiteSpace(snomedCode) ? null : snomedCode.Trim(),
            CollectionMode = collectionMode,
            DefaultMethod = string.IsNullOrWhiteSpace(defaultMethod) ? null : defaultMethod.Trim(),
            DefaultBodySite = string.IsNullOrWhiteSpace(defaultBodySite) ? null : defaultBodySite.Trim(),
            ContainerKind = containerKind,
            StabilityMinutes = stabilityMinutes,
            Discipline = discipline,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(
        string name,
        CollectionMode collectionMode,
        string? snomedCode,
        string? defaultMethod,
        string? defaultBodySite,
        ContainerKind containerKind,
        int? stabilityMinutes,
        LabDepartment discipline,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        CollectionMode = collectionMode;
        SnomedCode = string.IsNullOrWhiteSpace(snomedCode) ? null : snomedCode.Trim();
        DefaultMethod = string.IsNullOrWhiteSpace(defaultMethod) ? null : defaultMethod.Trim();
        DefaultBodySite = string.IsNullOrWhiteSpace(defaultBodySite) ? null : defaultBodySite.Trim();
        ContainerKind = containerKind;
        StabilityMinutes = stabilityMinutes;
        Discipline = discipline;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
