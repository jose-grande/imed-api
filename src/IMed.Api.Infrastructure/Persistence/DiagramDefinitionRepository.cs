using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Diagrams;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class DiagramDefinitionRepository(AppDbContext dbContext) : IDiagramDefinitionRepository
{
    public Task<DiagramDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.DiagramDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DiagramDefinition>> ListAsync(
        Guid? organizationId,
        bool includeNonPublished,
        CancellationToken cancellationToken = default)
    {
        IQueryable<DiagramDefinition> query = dbContext.DiagramDefinitions.AsNoTracking();

        if (organizationId.HasValue)
        {
            var orgId = organizationId.Value;
            query = query.Where(x => x.OrganizationId == orgId || x.OrganizationId == null);
        }

        if (!includeNonPublished)
        {
            query = query.Where(x => x.Status == DiagramStatus.Published);
        }

        return await query
            .OrderBy(x => x.Code)
            .ThenByDescending(x => x.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DiagramDefinition>> ResolveBySpecialtyAsync(
        Guid organizationId,
        string? specialtyCode,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(specialtyCode))
        {
            return [];
        }

        // Trae org-específica + preset global para la especialidad, ambos Published,
        // y dedup por code (prioriza la versión org-específica sobre el preset global).
        var candidates = await dbContext.DiagramDefinitions.AsNoTracking()
            .Where(x => (x.OrganizationId == organizationId || x.OrganizationId == null)
                && x.SpecialtyCode == specialtyCode
                && x.Status == DiagramStatus.Published)
            .OrderBy(x => x.Code)
            .ThenByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

        return candidates
            .GroupBy(x => x.Code)
            .Select(g => g.OrderByDescending(x => x.OrganizationId.HasValue).ThenByDescending(x => x.Version).First())
            .ToList();
    }

    public async Task<DiagramDefinition?> ResolveByCodeAndVersionAsync(
        Guid organizationId,
        string code,
        string version,
        CancellationToken cancellationToken = default)
    {
        var orgMatch = await dbContext.DiagramDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                  && x.Code == code
                  && x.Version == version,
                cancellationToken);
        if (orgMatch is not null)
        {
            return orgMatch;
        }

        return await dbContext.DiagramDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == null
                  && x.Code == code
                  && x.Version == version,
                cancellationToken);
    }

    public Task<bool> ExistsAsync(string code, string version, Guid? organizationId, CancellationToken cancellationToken = default)
        => dbContext.DiagramDefinitions
            .AsNoTracking()
            .AnyAsync(
                x => x.Code == code
                  && x.Version == version
                  && x.OrganizationId == organizationId,
                cancellationToken);

    public async Task AddAsync(DiagramDefinition diagram, CancellationToken cancellationToken = default)
    {
        dbContext.DiagramDefinitions.Add(diagram);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(DiagramDefinition diagram, CancellationToken cancellationToken = default)
    {
        if (dbContext.Entry(diagram).State == EntityState.Detached)
        {
            dbContext.DiagramDefinitions.Update(diagram);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var diagram = await dbContext.DiagramDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (diagram is null)
        {
            return;
        }
        dbContext.DiagramDefinitions.Remove(diagram);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
