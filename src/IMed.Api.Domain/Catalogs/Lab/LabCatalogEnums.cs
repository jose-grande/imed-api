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
