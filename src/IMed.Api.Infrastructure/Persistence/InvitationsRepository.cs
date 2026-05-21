using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Invitations;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class InvitationsRepository(AppDbContext dbContext) : IInvitationsRepository
{
    public Task<Invitation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Invitations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
    }

    public Task<Invitation?> GetPendingByEmailAndOrganizationAsync(
        string email,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;
        return dbContext.Invitations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                  && x.Email == normalized
                  && x.Status == InvitationStatus.Pending,
                cancellationToken);
    }

    public async Task<IReadOnlyList<Invitation>> ListPendingForOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Invitations
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == InvitationStatus.Pending)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Invitation>> ListPendingCreatedBeforeAsync(
        Guid organizationId,
        DateTimeOffset threshold,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.Invitations
            .Where(x => x.OrganizationId == organizationId
                     && x.Status == InvitationStatus.Pending
                     && x.CreatedAt < threshold)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        dbContext.Invitations.Add(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(Invitation invitation, CancellationToken cancellationToken = default)
    {
        dbContext.Invitations.Update(invitation);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
