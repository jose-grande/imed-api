using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Topes de dinero por usuario sobre EF Core (FAC-1/FAC-4).</summary>
public sealed class AuthorizationLimitRepository(AppDbContext db) : IAuthorizationLimitRepository
{
    public async Task<UserAuthorizationLimit?> FindEffectiveAsync(
        Guid organizationId,
        Guid userId,
        AuthorizationLimitKind kind,
        DateOnly day,
        CancellationToken ct = default)
    {
        var rows = await db.UserAuthorizationLimits.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.UserId == userId && x.Kind == kind)
            .OrderByDescending(x => x.ValidFrom)
            .ToListAsync(ct);

        // La vigencia se filtra en memoria: solapar dos topes es legítimo al reemplazar uno, y
        // con el orden por ValidFrom descendente gana el más reciente que rija ese día.
        return rows.FirstOrDefault(x => x.IsInEffectOn(day));
    }

    public async Task<IReadOnlyList<UserAuthorizationLimit>> ListForUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default) =>
        await db.UserAuthorizationLimits.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.UserId == userId)
            .OrderBy(x => x.Kind).ThenByDescending(x => x.ValidFrom)
            .ToListAsync(ct);

    public async Task AddAsync(UserAuthorizationLimit limit, CancellationToken ct = default)
    {
        db.UserAuthorizationLimits.Add(limit);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(UserAuthorizationLimit limit, CancellationToken ct = default)
    {
        db.UserAuthorizationLimits.Update(limit);
        await db.SaveChangesAsync(ct);
    }
}
