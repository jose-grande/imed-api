using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Forms;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class FormDefinitionRepository(AppDbContext dbContext) : IFormDefinitionRepository
{
    public Task<FormDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        => dbContext.FormDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<FormDefinition>> ListAsync(
        Guid? organizationId,
        bool includeNonPublished,
        CancellationToken cancellationToken = default)
    {
        IQueryable<FormDefinition> query = dbContext.FormDefinitions.AsNoTracking();

        // Filtro por org: incluye los presets globales (OrganizationId IS NULL) además de los de la org.
        if (organizationId.HasValue)
        {
            var orgId = organizationId.Value;
            query = query.Where(x => x.OrganizationId == orgId || x.OrganizationId == null);
        }

        if (!includeNonPublished)
        {
            query = query.Where(x => x.Status == FormStatus.Published);
        }

        return await query
            .OrderBy(x => x.Code)
            .ThenByDescending(x => x.Version)
            .ToListAsync(cancellationToken);
    }

    public async Task<FormDefinition?> ResolveBySpecialtyAsync(
        Guid organizationId,
        string? specialtyCode,
        CancellationToken cancellationToken = default)
    {
        // Orden de preferencia (ver spec form-definitions-schema.md §2):
        //   1. org-específica + specialty match
        //   2. preset global (org=null) + specialty match
        //   3. preset global "general" (specialty=null Y code="general")
        //   4. null
        FormDefinition? candidate = null;

        if (!string.IsNullOrWhiteSpace(specialtyCode))
        {
            // 1. org-específica
            candidate = await BestMatchAsync(
                dbContext.FormDefinitions.AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId
                        && x.SpecialtyCode == specialtyCode
                        && x.Status == FormStatus.Published),
                cancellationToken);
            if (candidate is not null)
            {
                return candidate;
            }

            // 2. preset global con specialty
            candidate = await BestMatchAsync(
                dbContext.FormDefinitions.AsNoTracking()
                    .Where(x => x.OrganizationId == null
                        && x.SpecialtyCode == specialtyCode
                        && x.Status == FormStatus.Published),
                cancellationToken);
            if (candidate is not null)
            {
                return candidate;
            }
        }

        // 3. fallback al "general"
        candidate = await BestMatchAsync(
            dbContext.FormDefinitions.AsNoTracking()
                .Where(x => x.OrganizationId == null
                    && x.Code == "general"
                    && x.Status == FormStatus.Published),
            cancellationToken);
        return candidate;
    }

    public async Task<FormDefinition?> ResolveByCodeAndVersionAsync(
        Guid organizationId,
        string code,
        string version,
        CancellationToken cancellationToken = default)
    {
        // Org-específica primero, fallback a preset global. Cualquier status —
        // queremos poder mostrar consultas viejas con forms retirados.
        var orgMatch = await dbContext.FormDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                  && x.Code == code
                  && x.Version == version,
                cancellationToken);
        if (orgMatch is not null)
        {
            return orgMatch;
        }

        return await dbContext.FormDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == null
                  && x.Code == code
                  && x.Version == version,
                cancellationToken);
    }

    public Task<bool> ExistsAsync(string code, string version, Guid? organizationId, CancellationToken cancellationToken = default)
        => dbContext.FormDefinitions
            .AsNoTracking()
            .AnyAsync(
                x => x.Code == code
                  && x.Version == version
                  && x.OrganizationId == organizationId,
                cancellationToken);

    public async Task AddAsync(FormDefinition form, CancellationToken cancellationToken = default)
    {
        dbContext.FormDefinitions.Add(form);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(FormDefinition form, CancellationToken cancellationToken = default)
    {
        // Si el caller obtuvo `form` via GetByIdAsync, ya está tracked.
        // Si lo construyó fresh, lo attacheamos.
        if (dbContext.Entry(form).State == EntityState.Detached)
        {
            dbContext.FormDefinitions.Update(form);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var form = await dbContext.FormDefinitions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (form is null)
        {
            return;
        }
        dbContext.FormDefinitions.Remove(form);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Toma la versión SemVer mayor de un set de candidatos. SemVer naïve por now (string).</summary>
    private static async Task<FormDefinition?> BestMatchAsync(
        IQueryable<FormDefinition> query,
        CancellationToken cancellationToken)
    {
        // Ordering por Version desc — naïve string compare. Mejorable a SemVer real con
        // funciones SQL custom o materializando version components, pero alcanza para
        // versiones simples "X.Y.Z" donde N<10. Hito 2 lo refina si crece.
        return await query
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
