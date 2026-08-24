using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Locations;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Implementación EF Core del eje de sede (ADR-008).</summary>
public sealed class LocationRepository(AppDbContext db) : ILocationRepository
{
    public async Task<IReadOnlyList<OrganizationLocation>> ListAsync(
        Guid organizationId, bool activeOnly = true, CancellationToken ct = default) =>
        await db.OrganizationLocations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && (!activeOnly || x.Active))
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.Name)
            .ToListAsync(ct);

    public Task<OrganizationLocation?> GetAsync(Guid organizationId, Guid locationId, CancellationToken ct = default) =>
        db.OrganizationLocations
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == locationId, ct);

    public Task<OrganizationLocation?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return db.OrganizationLocations
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Code == normalized, ct);
    }

    public Task<OrganizationLocation?> GetDefaultAsync(Guid organizationId, CancellationToken ct = default) =>
        db.OrganizationLocations
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct);

    public async Task AddAsync(OrganizationLocation location, CancellationToken ct = default)
    {
        db.OrganizationLocations.Add(location);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(OrganizationLocation location, CancellationToken ct = default)
    {
        db.OrganizationLocations.Update(location);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<(UserLocationAssignment Assignment, OrganizationLocation Location)>> ListForUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var rows = await db.UserLocations.AsNoTracking()
            .Where(a => a.OrganizationId == organizationId && a.UserId == userId)
            .Join(
                db.OrganizationLocations.AsNoTracking(),
                a => a.LocationId,
                l => l.Id,
                (a, l) => new { Assignment = a, Location = l })
            .Where(x => x.Location.Active)
            .OrderByDescending(x => x.Assignment.IsPrimary)
            .ThenBy(x => x.Location.Name)
            .ToListAsync(ct);

        return rows.Select(x => (x.Assignment, x.Location)).ToList();
    }

    public Task<UserLocationAssignment?> GetAssignmentAsync(
        Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default) =>
        db.UserLocations.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.UserId == userId && x.LocationId == locationId, ct);

    public async Task AddAssignmentAsync(UserLocationAssignment assignment, CancellationToken ct = default)
    {
        db.UserLocations.Add(assignment);
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAssignmentAsync(
        Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default)
    {
        var existing = await GetAssignmentAsync(organizationId, userId, locationId, ct);
        if (existing is null)
        {
            return;
        }
        db.UserLocations.Remove(existing);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetPrimaryAsync(
        Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default)
    {
        var assignments = await db.UserLocations
            .Where(x => x.OrganizationId == organizationId && x.UserId == userId)
            .ToListAsync(ct);

        foreach (var a in assignments)
        {
            if (a.LocationId == locationId)
            {
                a.MarkPrimary();
            }
            else
            {
                a.ClearPrimary();
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
