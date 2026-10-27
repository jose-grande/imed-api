namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Definición de una prueba de laboratorio (Épica LIS-1, L1-D3/L1-D7).
///
/// <para>Master-data del laboratorio. Anclada a LOINC (<see cref="LoincCode"/>)
/// que se reusa de Épica N. <see cref="OrganizationId"/> nullable: <c>null</c> =
/// plantilla global (seed mock HN), no-null = override por org (L1-D1).</para>
///
/// <para>La preparación/precondiciones (ayuno, recolección, instrucciones) las
/// lee la recepción (LIS-3) para verificar antes de la toma (LIS-DEC-14). Para
/// el MVP se aplanan en columnas; si crecen, migrar a jsonb.</para>
/// </summary>
public sealed class LabTestDefinition
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "GLU_FAST".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Código LOINC asociado (FK lógica a loinc_catalog). Opcional.</summary>
    public string? LoincCode { get; private set; }

    /// <summary>Nombre display. Ej: "Glucosa en ayunas".</summary>
    public string Name { get; private set; } = string.Empty;

    public LabTestDatatype Datatype { get; private set; } = LabTestDatatype.Numeric;

    /// <summary>Unidad UCUM. Ej: "mg/dL". Opcional (no aplica a coded/text/boolean).</summary>
    public string? Unit { get; private set; }

    public LabDepartment Department { get; private set; } = LabDepartment.Other;

    /// <summary>
    /// Mínimo de horas de ayuno requeridas. Null = la prueba no requiere ayuno.
    /// </summary>
    public int? FastingHoursMin { get; private set; }

    /// <summary>
    /// Máximo de horas de ayuno admitidas. Null = sin cota superior.
    /// </summary>
    /// <remarks>
    /// Es un rango y no un número porque así lo especifican los laboratorios: un perfil
    /// lipídico pide <b>9 a 12 horas</b>, y un ayuno de 20 h altera los triglicéridos tanto
    /// como uno de 4. Con un solo valor había que elegir: puesto en 12 se rechazaba a quien
    /// llegaba con 10; puesto en 9 pasaba quien llegaba con 20.
    ///
    /// <para>Una glucosa, en cambio, pide «al menos 8 h» y no tiene cota superior: por eso
    /// el máximo es opcional y no se asume igual al mínimo.</para>
    /// </remarks>
    public int? FastingHoursMax { get; private set; }

    /// <summary>¿Esta prueba exige ayuno?</summary>
    public bool RequiresFasting => FastingHoursMin is > 0;

    /// <summary>Tipo de recolección. Ej: "blood", "urine-spot", "urine-24h", "swab".</summary>
    public string? CollectionType { get; private set; }

    /// <summary>Instrucción al paciente (suspensión de medicamentos, etc.).</summary>
    public string? PatientInstruction { get; private set; }

    /// <summary>Tipo de muestra del catálogo (LIS-RD-26). Null mientras no se mapee.</summary>
    public Guid? SpecimenTypeId { get; private set; }

    /// <summary>Tubo de sangre (subtipo, LIS-RD-16). Null si no aplica (orina, hisopado…).</summary>
    public Guid? TubeTypeId { get; private set; }

    /// <summary>Override del modo de colección; null = hereda del tipo de muestra (LIS-RD-23).</summary>
    public CollectionMode? CollectionMode { get; private set; }

    // ── Requerimientos del plan de colección (R6-F1a, decisión UX §8.9) ──
    // Alimentan al motor de plan de colección (R6-F2): cuánta muestra consume la
    // prueba para derivar contenedores/volúmenes y consolidar por aditivo+capacidad.

    /// <summary>Volumen mínimo de muestra que requiere la prueba, en mL. Null = sin definir.</summary>
    public decimal? MinVolumeMl { get; private set; }

    /// <summary>Volumen preferido (con margen para repeticiones/reflejos), en mL.</summary>
    public decimal? PreferredVolumeMl { get; private set; }

    /// <summary>Requerimiento mínimo del analizador en mL (volumen muerto + consumo por
    /// corrida). Aproximación hasta que llegue el catálogo real de analizadores (LIS-12).</summary>
    public decimal? AnalyzerMinVolumeMl { get; private set; }

    /// <summary>Ventana de estabilidad de la prueba en minutos si difiere del tipo de
    /// muestra; null = hereda de <c>SpecimenType.StabilityMinutes</c>.</summary>
    public int? StabilityMinutes { get; private set; }

    /// <summary>Permite deprecar sin borrar para preservar referencias históricas.</summary>
    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabTestDefinition() { }

    public static LabTestDefinition Create(
        string code,
        string name,
        LabTestDatatype datatype = LabTestDatatype.Numeric,
        string? loincCode = null,
        string? unit = null,
        LabDepartment department = LabDepartment.Other,
        int? fastingHoursMin = null,
        int? fastingHoursMax = null,
        string? collectionType = null,
        string? patientInstruction = null,
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

        // Un rango invertido no es un dato raro, es un dato imposible: ninguna hora de
        // ayuno lo satisface, y la prueba quedaría inrecibible sin que nada lo explique.
        if (fastingHoursMin is int min && fastingHoursMax is int max && max < min)
        {
            throw new ArgumentException(
                $"El máximo de ayuno ({max} h) no puede ser menor que el mínimo ({min} h).",
                nameof(fastingHoursMax));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new LabTestDefinition
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim(),
            Name = name.Trim(),
            Datatype = datatype,
            Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
            Department = department,
            FastingHoursMin = fastingHoursMin,
            FastingHoursMax = fastingHoursMax,
            CollectionType = string.IsNullOrWhiteSpace(collectionType) ? null : collectionType.Trim(),
            PatientInstruction = string.IsNullOrWhiteSpace(patientInstruction) ? null : patientInstruction.Trim(),
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(
        string name,
        LabTestDatatype datatype,
        string? loincCode,
        string? unit,
        LabDepartment department,
        int? fastingHoursMin,
        int? fastingHoursMax,
        string? collectionType,
        string? patientInstruction,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }

        // Un rango invertido no es un dato raro, es un dato imposible: ninguna hora de
        // ayuno lo satisface, y la prueba quedaría inrecibible sin que nada lo explique.
        if (fastingHoursMin is int min && fastingHoursMax is int max && max < min)
        {
            throw new ArgumentException(
                $"El máximo de ayuno ({max} h) no puede ser menor que el mínimo ({min} h).",
                nameof(fastingHoursMax));
        }

        Name = name.Trim();
        Datatype = datatype;
        LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim();
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        Department = department;
        FastingHoursMin = fastingHoursMin;
        FastingHoursMax = fastingHoursMax;
        CollectionType = string.IsNullOrWhiteSpace(collectionType) ? null : collectionType.Trim();
        PatientInstruction = string.IsNullOrWhiteSpace(patientInstruction) ? null : patientInstruction.Trim();
        Touch(timeProvider);
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        Touch(timeProvider);
    }

    /// <summary>
    /// Asocia la prueba a su tipo de muestra y tubo (LIS-RD-16/26). El
    /// <paramref name="collectionMode"/> es un override; null = hereda del tipo.
    /// </summary>
    public void SetSpecimenMapping(
        Guid? specimenTypeId,
        Guid? tubeTypeId,
        CollectionMode? collectionMode,
        TimeProvider? timeProvider = null)
    {
        SpecimenTypeId = specimenTypeId;
        TubeTypeId = tubeTypeId;
        CollectionMode = collectionMode;
        Touch(timeProvider);
    }

    /// <summary>
    /// Define los requerimientos de volumen/estabilidad del plan de colección
    /// (R6-F1a). Separado de <see cref="Edit"/> para no tocar el flujo CRUD actual.
    /// </summary>
    public void SetCollectionRequirements(
        decimal? minVolumeMl,
        decimal? preferredVolumeMl,
        decimal? analyzerMinVolumeMl,
        int? stabilityMinutes,
        TimeProvider? timeProvider = null)
    {
        MinVolumeMl = minVolumeMl;
        PreferredVolumeMl = preferredVolumeMl;
        AnalyzerMinVolumeMl = analyzerMinVolumeMl;
        StabilityMinutes = stabilityMinutes;
        Touch(timeProvider);
    }

    private void Touch(TimeProvider? timeProvider) =>
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
}
