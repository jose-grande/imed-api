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

    /// <summary>Horas de ayuno requeridas. Null = no requiere ayuno.</summary>
    public int? FastingHours { get; private set; }

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
        int? fastingHours = null,
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
            FastingHours = fastingHours,
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
        int? fastingHours,
        string? collectionType,
        string? patientInstruction,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        Datatype = datatype;
        LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim();
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        Department = department;
        FastingHours = fastingHours;
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

    private void Touch(TimeProvider? timeProvider) =>
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
}
