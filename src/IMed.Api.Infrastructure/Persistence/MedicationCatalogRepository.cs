using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class MedicationCatalogRepository(AppDbContext dbContext) : IMedicationCatalogRepository
{
    public async Task<IReadOnlyList<MedicationCatalogEntry>> SearchAsync(
        string query,
        int limit,
        string? category = null,
        CancellationToken cancellationToken = default)
    {
        var term = query.Trim();
        var like = $"%{term}%";
        var codePrefix = $"{term}%";

        var q = dbContext.MedicationCatalog
            .AsNoTracking()
            .Where(x => x.Active);

        if (!string.IsNullOrWhiteSpace(category))
        {
            var cat = category.Trim().ToLowerInvariant();
            q = q.Where(x => x.Category == cat);
        }

        // Match: ATC prefix exact, o NameGeneric/NameCommercial con unaccent.
        // Code ATC tiene letras+números (ej. "N02BE01"); el prefix con ILIKE
        // alcanza. Para name usamos unaccent para que "metoclopramida"
        // matchee "metoclopramida" sin acentos.
        return await q
            .Where(x =>
                EF.Functions.ILike(x.AtcCode, codePrefix) ||
                EF.Functions.ILike(EF.Functions.Unaccent(x.NameGeneric), EF.Functions.Unaccent(like)) ||
                (x.NameCommercial != null && EF.Functions.ILike(EF.Functions.Unaccent(x.NameCommercial), EF.Functions.Unaccent(like))))
            .OrderByDescending(x => EF.Functions.ILike(x.AtcCode, codePrefix))
            .ThenBy(x => x.NameGeneric)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task<MedicationCatalogEntry?> GetByAtcCodeAsync(string atcCode, CancellationToken cancellationToken = default)
    {
        var normalized = atcCode.Trim();
        return dbContext.MedicationCatalog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.AtcCode == normalized, cancellationToken);
    }

    public Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        return dbContext.MedicationCatalog.CountAsync(cancellationToken);
    }
}
