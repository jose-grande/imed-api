using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.LabCatalog;

// ------------------------------- Pruebas -------------------------------

public sealed record SearchLabTestsQuery(Guid? OrganizationId, string? Term, string? Department, bool ActiveOnly, int Limit);

public sealed class SearchLabTestsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<LabTestDto>> HandleAsync(SearchLabTestsQuery query, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 200);
        var rows = await repo.SearchTestsAsync(query.OrganizationId, query.Term, query.Department, query.ActiveOnly, limit, cancellationToken);
        return rows.Select(LabTestDto.FromDomain).ToList();
    }
}

public sealed record GetLabTestByIdQuery(Guid Id);

public sealed class GetLabTestByIdHandler(ILabCatalogRepository repo)
{
    public async Task<LabTestDto?> HandleAsync(GetLabTestByIdQuery query, CancellationToken cancellationToken = default)
    {
        var t = await repo.GetTestByIdAsync(query.Id, cancellationToken);
        return t is null ? null : LabTestDto.FromDomain(t);
    }
}

// ------------------------------- Paneles -------------------------------

public sealed record ListLabPanelsQuery(Guid? OrganizationId);

public sealed class ListLabPanelsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<LabPanelDto>> HandleAsync(ListLabPanelsQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListPanelsAsync(query.OrganizationId, cancellationToken);
        return rows.Select(LabPanelDto.FromDomain).ToList();
    }
}

public sealed record GetLabPanelDetailQuery(Guid Id);

public sealed class GetLabPanelDetailHandler(ILabCatalogRepository repo)
{
    public async Task<LabPanelDetailDto?> HandleAsync(GetLabPanelDetailQuery query, CancellationToken cancellationToken = default)
    {
        var panel = await repo.GetPanelByIdAsync(query.Id, cancellationToken);
        if (panel is null)
        {
            return null;
        }

        var version = await repo.GetActiveVersionAsync(panel.Id, cancellationToken);
        var items = new List<LabPanelItemDto>();
        if (version is not null)
        {
            var versionItems = await repo.GetVersionItemsAsync(version.Id, cancellationToken);
            var tests = (await repo.GetTestsByIdsAsync(versionItems.Select(i => i.LabTestId).ToList(), cancellationToken))
                .ToDictionary(t => t.Id);
            items = versionItems
                .OrderBy(i => i.DisplayOrder)
                .Where(i => tests.ContainsKey(i.LabTestId))
                .Select(i =>
                {
                    var t = tests[i.LabTestId];
                    return new LabPanelItemDto(t.Id, t.Code, t.Name, t.Unit, i.DisplayOrder);
                })
                .ToList();
        }

        return new LabPanelDetailDto(
            panel.Id, panel.OrganizationId, panel.Code, panel.Name, panel.Description, panel.Active,
            version?.Version, version?.EffectiveDate, items);
    }
}

// -------------------------- Rangos de referencia -----------------------

public sealed record ListReferenceRangesQuery(Guid? OrganizationId, Guid TestId);

public sealed class ListReferenceRangesHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<ReferenceRangeDto>> HandleAsync(ListReferenceRangesQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListRangesByTestAsync(query.OrganizationId, query.TestId, cancellationToken);
        return rows.Select(ReferenceRangeDto.FromDomain).ToList();
    }
}

// ----------------------- Fórmulas calculadas / TAT ---------------------

public sealed record ListDerivedCalculationsQuery(Guid? OrganizationId);

public sealed class ListDerivedCalculationsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<DerivedCalculationDto>> HandleAsync(ListDerivedCalculationsQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListDerivedAsync(query.OrganizationId, cancellationToken);
        return rows.Select(DerivedCalculationDto.FromDomain).ToList();
    }
}

public sealed record ListTatTargetsQuery(Guid? OrganizationId);

public sealed class ListTatTargetsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<TatTargetDto>> HandleAsync(ListTatTargetsQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListTatTargetsAsync(query.OrganizationId, cancellationToken);
        return rows.Select(TatTargetDto.FromDomain).ToList();
    }
}
