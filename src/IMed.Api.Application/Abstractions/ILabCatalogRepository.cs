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

    // ---- Ofertas por sede ----
    Task<LabTestOffering?> GetOfferingAsync(Guid organizationId, Guid testId, CancellationToken cancellationToken = default);
    Task UpsertOfferingAsync(LabTestOffering offering, CancellationToken cancellationToken = default);

    // ---- Fórmulas calculadas / TAT (read-only en LIS-1) ----
    Task<IReadOnlyList<DerivedCalculation>> ListDerivedAsync(Guid? organizationId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TatTarget>> ListTatTargetsAsync(Guid? organizationId, CancellationToken cancellationToken = default);
}
