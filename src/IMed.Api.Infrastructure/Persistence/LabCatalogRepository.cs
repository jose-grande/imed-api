using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación EF Core del catálogo de laboratorio (Épica LIS-1, L1-F3).
/// Lecturas resuelven global (org_id NULL) + override por org, deduplicando por
/// code y prefiriendo la fila específica de la org cuando coexisten (L1-D1).
/// </summary>
public sealed class LabCatalogRepository(AppDbContext db) : ILabCatalogRepository
{
    // ----------------------------- Pruebas -----------------------------

    public async Task<IReadOnlyList<LabTestDefinition>> SearchTestsAsync(
        Guid? organizationId, string? term, string? department, bool activeOnly, int limit,
        CancellationToken cancellationToken = default)
    {
        var q = db.LabTests.AsNoTracking()
            .Where(x => x.OrganizationId == null || x.OrganizationId == organizationId);

        if (activeOnly)
        {
            q = q.Where(x => x.Active);
        }
        if (!string.IsNullOrWhiteSpace(department) && Enum.TryParse<LabDepartment>(department, ignoreCase: true, out var dept))
        {
            q = q.Where(x => x.Department == dept);
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            var t = term.Trim();
            var like = $"%{t}%";
            var codePrefix = $"{t}%";
            q = q.Where(x =>
                EF.Functions.ILike(x.Code, codePrefix) ||
                EF.Functions.ILike(EF.Functions.Unaccent(x.Name), EF.Functions.Unaccent(like)));
        }

        var rows = await q.OrderBy(x => x.Name).ToListAsync(cancellationToken);
        return DedupeByCodePreferOrg(rows, organizationId, x => x.Code, x => x.OrganizationId)
            .Take(limit)
            .ToList();
    }

    public Task<LabTestDefinition?> GetTestByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.LabTests.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<LabTestDefinition?> GetTestByLoincAsync(
        Guid? organizationId, string loincCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(loincCode))
        {
            return null;
        }
        var code = loincCode.Trim();
        var rows = await db.LabTests.AsNoTracking()
            .Where(x => x.LoincCode == code && (x.OrganizationId == null || x.OrganizationId == organizationId))
            .ToListAsync(cancellationToken);
        // Override de org sobre global.
        return rows.OrderByDescending(x => x.OrganizationId == organizationId && organizationId != null).FirstOrDefault();
    }

    public Task<bool> TestCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default)
    {
        var c = code.Trim();
        return db.LabTests.AnyAsync(x => x.OrganizationId == organizationId && x.Code == c, cancellationToken);
    }

    public async Task<IReadOnlyList<LabTestDefinition>> GetTestsByIdsAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }
        return await db.LabTests.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(cancellationToken);
    }

    public async Task AddTestAsync(LabTestDefinition test, CancellationToken cancellationToken = default)
    {
        db.LabTests.Add(test);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateTestAsync(LabTestDefinition test, CancellationToken cancellationToken = default)
    {
        db.LabTests.Update(test);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ----------------------------- Paneles -----------------------------

    public async Task<IReadOnlyList<LabPanel>> ListPanelsAsync(
        Guid? organizationId, CancellationToken cancellationToken = default)
    {
        var rows = await db.LabPanels.AsNoTracking()
            .Where(x => x.OrganizationId == null || x.OrganizationId == organizationId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return DedupeByCodePreferOrg(rows, organizationId, x => x.Code, x => x.OrganizationId).ToList();
    }

    public Task<LabPanel?> GetPanelByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.LabPanels.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<bool> PanelCodeExistsAsync(Guid? organizationId, string code, CancellationToken cancellationToken = default)
    {
        var c = code.Trim();
        return db.LabPanels.AnyAsync(x => x.OrganizationId == organizationId && x.Code == c, cancellationToken);
    }

    public async Task AddPanelAsync(LabPanel panel, CancellationToken cancellationToken = default)
    {
        db.LabPanels.Add(panel);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<LabPanelVersion?> GetActiveVersionAsync(Guid panelId, CancellationToken cancellationToken = default) =>
        db.LabPanelVersions.AsNoTracking()
            .Where(x => x.LabPanelId == panelId && x.Status == PanelVersionStatus.Active)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<int> GetMaxVersionNumberAsync(Guid panelId, CancellationToken cancellationToken = default)
    {
        var any = await db.LabPanelVersions.AnyAsync(x => x.LabPanelId == panelId, cancellationToken);
        if (!any)
        {
            return 0;
        }
        return await db.LabPanelVersions
            .Where(x => x.LabPanelId == panelId)
            .MaxAsync(x => x.Version, cancellationToken);
    }

    public async Task<IReadOnlyList<LabPanelVersionItem>> GetVersionItemsAsync(
        Guid versionId, CancellationToken cancellationToken = default) =>
        await db.LabPanelVersionItems.AsNoTracking()
            .Where(x => x.LabPanelVersionId == versionId)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(cancellationToken);

    public async Task AddVersionWithItemsAsync(
        LabPanelVersion version, IReadOnlyList<LabPanelVersionItem> items, CancellationToken cancellationToken = default)
    {
        // Retira las versiones activas previas (tracked) antes de activar la nueva.
        var actives = await db.LabPanelVersions
            .Where(x => x.LabPanelId == version.LabPanelId && x.Status == PanelVersionStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var v in actives)
        {
            v.SetStatus(PanelVersionStatus.Retired);
        }

        db.LabPanelVersions.Add(version);
        db.LabPanelVersionItems.AddRange(items);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ----------------------- Rangos de referencia ----------------------

    public async Task<IReadOnlyList<ReferenceRange>> ListRangesByTestAsync(
        Guid? organizationId, Guid testId, CancellationToken cancellationToken = default) =>
        await db.ReferenceRanges.AsNoTracking()
            .Where(x => x.LabTestId == testId && (x.OrganizationId == null || x.OrganizationId == organizationId))
            .OrderBy(x => x.Sex).ThenBy(x => x.AgeMinYears)
            .ToListAsync(cancellationToken);

    public Task<ReferenceRange?> GetRangeByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        db.ReferenceRanges.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddRangeAsync(ReferenceRange range, CancellationToken cancellationToken = default)
    {
        db.ReferenceRanges.Add(range);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateRangeAsync(ReferenceRange range, CancellationToken cancellationToken = default)
    {
        db.ReferenceRanges.Update(range);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ------------------------- Ofertas por sede ------------------------

    public Task<LabTestOffering?> GetOfferingAsync(
        Guid organizationId, Guid testId, CancellationToken cancellationToken = default) =>
        db.LabTestOfferings.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.LabTestId == testId, cancellationToken);

    public async Task UpsertOfferingAsync(LabTestOffering offering, CancellationToken cancellationToken = default)
    {
        var existing = await db.LabTestOfferings.FirstOrDefaultAsync(
            x => x.OrganizationId == offering.OrganizationId && x.LabTestId == offering.LabTestId, cancellationToken);
        if (existing is null)
        {
            db.LabTestOfferings.Add(offering);
        }
        else
        {
            existing.SetOffered(offering.Offered);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // --------------------- Fórmulas calculadas / TAT -------------------

    public async Task<IReadOnlyList<DerivedCalculation>> ListDerivedAsync(
        Guid? organizationId, CancellationToken cancellationToken = default) =>
        await db.DerivedCalculations.AsNoTracking()
            .Where(x => x.OrganizationId == null || x.OrganizationId == organizationId)
            .OrderBy(x => x.OutputTestCode)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<TatTarget>> ListTatTargetsAsync(
        Guid? organizationId, CancellationToken cancellationToken = default) =>
        await db.TatTargets.AsNoTracking()
            .Where(x => x.OrganizationId == null || x.OrganizationId == organizationId)
            .OrderBy(x => x.TargetCode).ThenBy(x => x.Priority)
            .ToListAsync(cancellationToken);

    // ------------------------------ helpers ----------------------------

    /// <summary>
    /// Cuando coexisten una fila global (org null) y una override de la org con el
    /// mismo code, la override gana (L1-D1). En LIS-1 aún no hay UI de clonado, así
    /// que en la práctica solo hay globales — pero la lógica deja el modelo listo.
    /// </summary>
    private static IEnumerable<T> DedupeByCodePreferOrg<T>(
        IEnumerable<T> rows, Guid? organizationId, Func<T, string> codeOf, Func<T, Guid?> orgOf) =>
        rows
            .GroupBy(codeOf)
            .Select(g => g.OrderByDescending(x => orgOf(x) == organizationId && organizationId != null).First());
}
