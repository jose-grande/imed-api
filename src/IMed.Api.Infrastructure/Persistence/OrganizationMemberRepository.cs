using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class OrganizationMemberRepository(AppDbContext dbContext) : IOrganizationMemberRepository
{
    public Task<OrganizationMember?> GetAsync(
        Guid keycloakUserId, Guid organizationId, CancellationToken cancellationToken = default) =>
        dbContext.OrganizationMembers
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.KeycloakUserId == keycloakUserId && x.OrganizationId == organizationId,
                cancellationToken);

    public async Task<IReadOnlyList<OrganizationMember>> ListByOrganizationAsync(
        Guid organizationId, CancellationToken cancellationToken = default) =>
        await dbContext.OrganizationMembers
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(OrganizationMember member, CancellationToken cancellationToken = default)
    {
        dbContext.OrganizationMembers.Add(member);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(member).State = EntityState.Detached;
            // Dos pestañas iniciando sesión a la vez: la otra ganó la carrera. El registro es
            // idempotente, así que no es un error; cualquier otra falla sí lo es.
            if (!await ExistsAsync(member, cancellationToken))
            {
                throw;
            }
        }
    }

    public async Task UpdateAsync(OrganizationMember member, CancellationToken cancellationToken = default)
    {
        dbContext.OrganizationMembers.Update(member);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task<bool> ExistsAsync(OrganizationMember member, CancellationToken cancellationToken) =>
        dbContext.OrganizationMembers.AsNoTracking().AnyAsync(
            x => x.KeycloakUserId == member.KeycloakUserId && x.OrganizationId == member.OrganizationId,
            cancellationToken);
}
