using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class SpecialtyRepository(AppDbContext dbContext) : ISpecialtyRepository
{
    public async Task<IReadOnlyList<SpecialtyCode>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var term = query.Trim();
        var like = $"%{term}%";

        // SNOMED codes son numéricos; matcheamos prefix exacto (no upper).
        return await dbContext.SpecialtyCatalog
            .AsNoTracking()
            .Where(x => x.Active && (
                EF.Functions.Like(x.Code, $"{term}%") ||
                EF.Functions.ILike(x.DisplayEs, like)))
            .OrderByDescending(x => EF.Functions.Like(x.Code, $"{term}%"))
            .ThenBy(x => x.DisplayEs)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public Task<SpecialtyCode?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim();
        return dbContext.SpecialtyCatalog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, cancellationToken);
    }
}
