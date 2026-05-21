using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Practitioners;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class UserPractitionerAssignmentRepository(AppDbContext dbContext) : IUserPractitionerAssignmentRepository
{
    public Task<UserPractitionerAssignment?> GetAsync(
        Guid keycloakUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserPractitionerAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.KeycloakUserId == keycloakUserId && x.OrganizationId == organizationId,
                cancellationToken);
    }

    public Task<UserPractitionerAssignment?> GetByPractitionerFhirIdAsync(
        string practitionerFhirId,
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.UserPractitionerAssignments
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.PractitionerFhirId == practitionerFhirId && x.OrganizationId == organizationId,
                cancellationToken);
    }

    public async Task<IReadOnlyList<UserPractitionerAssignment>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.UserPractitionerAssignments
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(UserPractitionerAssignment assignment, CancellationToken cancellationToken = default)
    {
        dbContext.UserPractitionerAssignments.Add(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(UserPractitionerAssignment assignment, CancellationToken cancellationToken = default)
    {
        dbContext.UserPractitionerAssignments.Update(assignment);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
