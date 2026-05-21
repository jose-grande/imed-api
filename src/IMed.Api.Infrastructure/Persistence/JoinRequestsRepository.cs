using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.JoinRequests;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class JoinRequestsRepository(AppDbContext dbContext) : IJoinRequestsRepository
{
    public Task<JoinRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.JoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public Task<JoinRequest?> GetPendingByEmailAndOrganizationAsync(
        string email,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;
        return dbContext.JoinRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                  && x.Email == normalized
                  && x.Status == JoinRequestStatus.Pending,
                cancellationToken);
    }

    public async Task<IReadOnlyList<JoinRequest>> ListPendingForOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.JoinRequests
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == JoinRequestStatus.Pending)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(JoinRequest joinRequest, CancellationToken cancellationToken = default)
    {
        dbContext.JoinRequests.Add(joinRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(JoinRequest joinRequest, CancellationToken cancellationToken = default)
    {
        dbContext.JoinRequests.Update(joinRequest);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
