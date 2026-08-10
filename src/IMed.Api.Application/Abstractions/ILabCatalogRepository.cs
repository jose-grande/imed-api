using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Repositorio del catálogo de laboratorio (Épica LIS-1). Cohesiona el master-data
/// (pruebas, paneles+versiones, rangos, ofertas, fórmulas, TAT). Las lecturas
/// resuelven global (org_id NULL) + override por org (L1-D1).
/// </summary>
public interface ILabCatalogRepository
{
    // ---- Pruebas ----
    Task<IReadOnlyList<LabTestDefinition>> SearchTestsAsync(
        Guid? organizationId, string? term, string? department, bool activeOnly, int limit,
        CancellationToken cancellationToken = default);
    Task<LabTestDefinition?> GetTestByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Resuelve una prueba por código LOINC (override de org sobre global). Usada por el enrutamiento (LIS-3).</summary>
    Task<LabTestDefinition?> GetTestByLoincAsync(Guid? organizationId, string loincCode, CancellationToken cancellationToken = default);
    Task<bool> TestCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabTestDefinition>> GetTestsByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    Task AddTestAsync(LabTestDefinition test, CancellationToken cancellationToken = default);
    Task UpdateTestAsync(LabTestDefinition test, CancellationToken cancellationToken = default);

    // ---- Paneles ----
    Task<IReadOnlyList<LabPanel>> ListPanelsAsync(Guid? organizationId, CancellationToken cancellationToken = default);
    Task<LabPanel?> GetPanelByIdAsync(Guid id, CancellationToken cancellationToken = default);
    /// <summary>Panel activo cuyo Code o LoincCode coincide (recepción: expansión de orden-panel, R3).</summary>
    Task<LabPanel?> GetPanelByCodeOrLoincAsync(Guid? organizationId, string codeOrLoinc, CancellationToken cancellationToken = default);
    Task<bool> PanelCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default);
    Task AddPanelAsync(LabPanel panel, CancellationToken cancellationToken = default);
    Task<LabPanelVersion?> GetActiveVersionAsync(Guid panelId, CancellationToken cancellationToken = default);
    Task<int> GetMaxVersionNumberAsync(Guid panelId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LabPanelVersionItem>> GetVersionItemsAsync(Guid versionId, CancellationToken cancellationToken = default);
    /// <summary>Agrega una versión nueva + items y retira las versiones activas previas del mismo panel (atómico).</summary>
    Task AddVersionWithItemsAsync(LabPanelVersion version, IReadOnlyList<LabPanelVersionItem> items, CancellationToken cancellationToken = default);

    // ---- Rangos de referencia ----
    Task<IReadOnlyList<ReferenceRange>> ListRangesByTestAsync(Guid? organizationId, Guid testId, CancellationToken cancellationToken = default);
    Task<ReferenceRange?> GetRangeByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddRangeAsync(ReferenceRange range, CancellationToken cancellationToken = default);
    Task UpdateRangeAsync(ReferenceRange range, CancellationToken cancellationToken = default);
    Task DeleteRangeAsync(ReferenceRange range, CancellationToken cancellationToken = default);

    // ---- Tipos de muestra / tubos (LIS-RD-26/16) ----
    Task<IReadOnlyList<SpecimenType>> ListSpecimenTypesAsync(Guid? organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TubeType>> ListTubeTypesAsync(Guid? organizationId, CancellationToken cancellationToken = default);

    // ---- Motivos de rechazo de muestra (R6-F1a) ----
    Task<IReadOnlyList<SpecimenRejectionReason>> ListSpecimenRejectionReasonsAsync(Guid? organizationId, CancellationToken cancellationToken = default);

    // ---- Formatos de etiqueta (R6-F4b) ----
    Task<IReadOnlyList<LabLabelFormat>> ListLabelFormatsAsync(Guid? organizationId, bool includeInactive = false, CancellationToken cancellationToken = default);
    Task<LabLabelFormat?> GetLabelFormatByIdAsync(Guid organizationId, Guid id, CancellationToken cancellationToken = default);
    Task AddLabelFormatAsync(LabLabelFormat format, CancellationToken cancellationToken = default);
    Task UpdateLabelFormatAsync(LabLabelFormat format, CancellationToken cancellationToken = default);

    /// <summary>Quita la marca de default a los demás formatos de la org (solo uno puede serlo).</summary>
    Task ClearDefaultLabelFormatAsync(Guid organizationId, Guid exceptId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LabLabelFormatAssignment>> ListLabelFormatAssignmentsAsync(Guid? organizationId, CancellationToken cancellationToken = default);

    /// <summary>Crea o reapunta la asignación de un tubo/tipo de muestra a un formato.</summary>
    Task UpsertLabelFormatAssignmentAsync(Guid organizationId, string? tubeTypeCode, string? specimenTypeCode, Guid labelFormatId, CancellationToken cancellationToken = default);

    /// <summary>Borra la asignación de un tubo/tipo de muestra (vuelve al formato default).</summary>
    Task DeleteLabelFormatAssignmentAsync(Guid organizationId, string? tubeTypeCode, string? specimenTypeCode, CancellationToken cancellationToken = default);

    // ---- Ofertas por sede ----
    Task<LabTestOffering?> GetOfferingAsync(Guid organizationId, Guid testId, CancellationToken cancellationToken = default);
    Task UpsertOfferingAsync(LabTestOffering offering, CancellationToken cancellationToken = default);

    // ---- Fórmulas calculadas / TAT (read-only en LIS-1) ----
    Task<IReadOnlyList<DerivedCalculation>> ListDerivedAsync(Guid? organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TatTarget>> ListTatTargetsAsync(Guid? organizationId, CancellationToken cancellationToken = default);
}
