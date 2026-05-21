using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class AllergyCatalogRepository(AppDbContext dbContext) : IAllergyCatalogRepository
{
    public async Task<IReadOnlyList<AllergyCatalogEntry>> SearchAsync(
        string query,
        int limit,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var term = query.Trim();
        var like = $"%{term}%";
        var codePrefix = $"{term}%";

        var q = dbContext.AllergyCatalog
            .AsNoTracking()
            .Where(x => x.Active);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim().ToLowerInvariant();
            q = q.Where(x => x.Category == cat);
        }

        // Display con unaccent — el typeahead matchea "mani" ↔ "maní" sin
        // diferenciar tildes. La extensión `unaccent` ya está habilitada en
        // la BD (migración AddUnaccentAndDropIcd10). Code es SNOMED numérico,
        // no necesita unaccent.
        return await q
            .Where(x =>
                EF.Functions.Like(x.Code, codePrefix) ||
                EF.Functions.ILike(EF.Functions.Unaccent(x.DisplayEs), EF.Functions.Unaccent(like)))
            .OrderByDescending(x => EF.Functions.Like(x.Code, codePrefix))
            .ThenBy(x => x.DisplayEs)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task<AllergyCatalogEntry?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim();
        return dbContext.AllergyCatalog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, cancellationToken);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.AllergyCatalog.CountAsync(cancellationToken);
    }
}
