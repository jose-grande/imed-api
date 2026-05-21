using IMed.Api.Application.Profile;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class OrganizationProfileRepository(AppDbContext dbContext) : IOrganizationProfileRepository
{
    public async Task<OrganizationProfile?> GetByIdAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        return await dbContext.OrganizationSubscriptions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => new OrganizationProfile(
                x.OrganizationId,
                x.Name,
                x.FhirId,
                x.ContractStatus.ToString().ToUpperInvariant(),
                x.TrialEndsAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<OrganizationProfile?> GetByFhirIdAsync(string fhirId, CancellationToken cancellationToken = default)
    {
        return await dbContext.OrganizationSubscriptions
            .AsNoTracking()
            .Where(x => x.FhirId == fhirId)
            .Select(x => new OrganizationProfile(
                x.OrganizationId,
                x.Name,
                x.FhirId,
                x.ContractStatus.ToString().ToUpperInvariant(),
                x.TrialEndsAt))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Guid?> GetFounderUserIdAsync(Guid organizationId, CancellationToken cancellationToken = default)
    {
        // El founder = primer assignment (cronológico) en user_tier_assignments
        // de la org. BootstrapOrganization persiste exactamente uno con
        // AssignedBy=KeycloakUserId del fundador.
        var founder = await dbContext.UserTierAssignments
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.AssignedAt)
            .Select(x => (Guid?)x.KeycloakUserId)
            .FirstOrDefaultAsync(cancellationToken);
        return founder;
    }
}
