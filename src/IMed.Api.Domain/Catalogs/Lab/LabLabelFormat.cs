namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Formato físico de una etiqueta de muestra (R6-F4b). El producto se vende a
/// laboratorios que compran impresoras distintas (Zebra, Brother, TSC…), así que el
/// tamaño **no puede ser una constante del código**: cada laboratorio parametriza los
/// rollos que usa y el sistema genera el PDF con la página de ese tamaño exacto.
///
/// <para>Los tamaños son los estándar de la industria (2"×1", 3"×1", cryovial…), pero
/// el laboratorio puede editarlos y agregar los suyos. Los <c>Offset*</c> existen
/// porque las térmicas casi siempre necesitan calibración fina: el rollo rara vez
/// queda perfectamente alineado con el cabezal.</para>
///
/// <para>Global-vs-org como el resto del catálogo (L1-D1): las filas con
/// <c>OrganizationId == null</c> son la plantilla del seed que
/// <c>LabCatalogProvisioning</c> clona a cada org nueva (copy-on-create, rev 53).</para>
/// </summary>
public sealed class LabLabelFormat
{
    /// <summary>Dimensión máxima aceptada (mm). Cota de sanidad: una etiqueta de muestra
    /// no supera el ancho de una hoja A4; atrapa errores de unidades (px/pulgadas por mm).</summary>
    public const decimal MaxDimensionMm = 300m;

    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "TUBE-51X25", "CRYO-33X13".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Nombre display. Ej: "Tubo estándar 51 × 25 mm".</summary>
    public string Name { get; private set; } = string.Empty;

    public decimal WidthMm { get; private set; }
    public decimal HeightMm { get; private set; }

    /// <summary>Margen interno (mm) entre el borde del papel y el contenido.</summary>
    public decimal MarginMm { get; private set; }

    /// <summary>Corrimiento horizontal de calibración (mm). Positivo = mueve a la derecha.</summary>
    public decimal OffsetXMm { get; private set; }

    /// <summary>Corrimiento vertical de calibración (mm). Positivo = mueve hacia abajo.</summary>
    public decimal OffsetYMm { get; private set; }

    /// <summary>Formato de respaldo cuando el contenedor no tiene asignación
    /// (tubo/tipo de muestra sin formato explícito). Una sola por org.</summary>
    public bool IsDefault { get; private set; }

    public int SortOrder { get; private set; }

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabLabelFormat() { }

    public static LabLabelFormat Create(
        string code,
        string name,
        decimal widthMm,
        decimal heightMm,
        decimal marginMm = 1.5m,
        decimal offsetXMm = 0m,
        decimal offsetYMm = 0m,
        bool isDefault = false,
        int sortOrder = 0,
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
        ValidateDimensions(widthMm, heightMm, marginMm);

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabLabelFormat
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            WidthMm = widthMm,
            HeightMm = heightMm,
            MarginMm = marginMm,
            OffsetXMm = offsetXMm,
            OffsetYMm = offsetYMm,
            IsDefault = isDefault,
            SortOrder = sortOrder,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(
        string name,
        decimal widthMm,
        decimal heightMm,
        decimal marginMm,
        decimal offsetXMm,
        decimal offsetYMm,
        int sortOrder,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        ValidateDimensions(widthMm, heightMm, marginMm);

        Name = name.Trim();
        WidthMm = widthMm;
        HeightMm = heightMm;
        MarginMm = marginMm;
        OffsetXMm = offsetXMm;
        OffsetYMm = offsetYMm;
        SortOrder = sortOrder;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetDefault(bool isDefault, TimeProvider? timeProvider = null)
    {
        IsDefault = isDefault;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static void ValidateDimensions(decimal widthMm, decimal heightMm, decimal marginMm)
    {
        if (widthMm <= 0 || widthMm > MaxDimensionMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(widthMm), widthMm, $"El ancho debe estar entre 0 y {MaxDimensionMm} mm.");
        }
        if (heightMm <= 0 || heightMm > MaxDimensionMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heightMm), heightMm, $"El alto debe estar entre 0 y {MaxDimensionMm} mm.");
        }
        if (marginMm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(marginMm), marginMm, "El margen no puede ser negativo.");
        }
        // El margen se aplica a ambos lados: si se come el papel entero no queda área útil.
        if (marginMm * 2 >= widthMm || marginMm * 2 >= heightMm)
        {
            throw new ArgumentOutOfRangeException(
                nameof(marginMm), marginMm, "El margen no deja área útil dentro de la etiqueta.");
        }
    }
}

/// <summary>
/// Asigna un formato de etiqueta a un tipo de tubo o a un tipo de muestra (R6-F4b).
///
/// <para><b>Por qué es una tabla aparte y no un FK en <c>tube_type</c>:</b> los tipos de
/// tubo/muestra son <b>referencia estándar compartida</b> entre tenants — el seed los crea
/// con <c>organization_id = null</c> y <c>LabCatalogProvisioning</c> deliberadamente
/// <b>no los clona</b> (rev 53). Poner el formato ahí haría que un laboratorio cambiando
/// "el tubo lila usa etiqueta wrap" se lo cambiara a todos los demás. La asignación vive
/// entonces por organización y referencia el tubo/muestra <b>por código</b>, que es además
/// lo que el contenedor del plan de colección ya guarda.</para>
///
/// <para>Se resuelve en cascada: asignación por tubo → asignación por tipo de muestra
/// (cubre los recipientes sin tubo, como el frasco de orina) → formato default de la org.</para>
/// </summary>
public sealed class LabLabelFormatAssignment
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código del tipo de tubo (<c>tube_type.code</c>). Excluyente con <see cref="SpecimenTypeCode"/>.</summary>
    public string? TubeTypeCode { get; private set; }

    /// <summary>Código del tipo de muestra (<c>specimen_type.code</c>). Excluyente con <see cref="TubeTypeCode"/>.</summary>
    public string? SpecimenTypeCode { get; private set; }

    public Guid LabLabelFormatId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabLabelFormatAssignment() { }

    public static LabLabelFormatAssignment ForTube(
        string tubeTypeCode, Guid labelFormatId, Guid? organizationId = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(tubeTypeCode))
        {
            throw new ArgumentException("TubeTypeCode es obligatorio.", nameof(tubeTypeCode));
        }
        return Build(tubeTypeCode.Trim(), null, labelFormatId, organizationId, timeProvider);
    }

    public static LabLabelFormatAssignment ForSpecimen(
        string specimenTypeCode, Guid labelFormatId, Guid? organizationId = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(specimenTypeCode))
        {
            throw new ArgumentException("SpecimenTypeCode es obligatorio.", nameof(specimenTypeCode));
        }
        return Build(null, specimenTypeCode.Trim(), labelFormatId, organizationId, timeProvider);
    }

    /// <summary>Reapunta la asignación a otro formato (el laboratorio cambió de rollo).</summary>
    public void Reassign(Guid labelFormatId, TimeProvider? timeProvider = null)
    {
        if (labelFormatId == Guid.Empty)
        {
            throw new ArgumentException("LabelFormatId es obligatorio.", nameof(labelFormatId));
        }
        LabLabelFormatId = labelFormatId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static LabLabelFormatAssignment Build(
        string? tubeTypeCode, string? specimenTypeCode, Guid labelFormatId, Guid? organizationId, TimeProvider? timeProvider)
    {
        if (labelFormatId == Guid.Empty)
        {
            throw new ArgumentException("LabelFormatId es obligatorio.", nameof(labelFormatId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabLabelFormatAssignment
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            TubeTypeCode = tubeTypeCode,
            SpecimenTypeCode = specimenTypeCode,
            LabLabelFormatId = labelFormatId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
