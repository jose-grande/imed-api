namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>Tipo de dato del resultado de una prueba de laboratorio.</summary>
public enum LabTestDatatype
{
    Numeric = 0,
    Count = 1,
    Text = 2,
    Coded = 3,
    Boolean = 4,
}

/// <summary>Área/departamento del laboratorio que procesa la prueba.</summary>
public enum LabDepartment
{
    Hematology = 0,
    Chemistry = 1,
    Immunology = 2,
    Microbiology = 3,
    Molecular = 4,
    Urinalysis = 5,
    Other = 6,
}

/// <summary>Sexo al que aplica un rango de referencia.</summary>
public enum ReferenceSex
{
    Any = 0,
    Male = 1,
    Female = 2,
}

/// <summary>Ciclo de vida de una versión de panel (L1-D4).</summary>
public enum PanelVersionStatus
{
    Draft = 0,
    Active = 1,
    Retired = 2,
}

/// <summary>Alcance de un objetivo de TAT.</summary>
public enum TatScope
{
    Test = 0,
    Panel = 1,
}

/// <summary>
/// Prioridad de la orden. Espeja el ValueSet FHIR <c>request-priority</c>
/// (<c>ServiceRequest.priority</c>) — la cola del LIS la consume (LIS-DEC-14).
/// </summary>
public enum LabRequestPriority
{
    Routine = 0,
    Urgent = 1,
    Asap = 2,
    Stat = 3,
}

/// <summary>
/// Modo de colección de una muestra (LIS-RD-23). Distingue lo que el paciente
/// trae ya tomado de lo que se toma en el laboratorio.
/// </summary>
public enum CollectionMode
{
    /// <summary>El paciente la trae ya tomada (orina espontánea, heces).</summary>
    PatientCollected = 0,

    /// <summary>Se toma en el laboratorio (sangre, hisopado, OGTT). Va a la cola de toma.</summary>
    OnSite = 1,
}

/// <summary>
/// Tipo de recipiente de la muestra (LIS-RD-16/26). El tubo de sangre es el caso
/// particular (<see cref="TubeType"/>); aquí se generaliza a frasco, hisopo, etc.
/// </summary>
public enum ContainerKind
{
    Tube = 0,
    Cup = 1,
    SterileContainer = 2,
    Swab = 3,
    Slide = 4,
    Device = 5,
    Other = 6,
}
