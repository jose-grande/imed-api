using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class BillingSettingsRepository(AppDbContext db) : IBillingSettingsRepository
{
    public async Task<BillingSettings> GetAsync(Guid organizationId, CancellationToken ct = default) =>
        await db.BillingSettings.AsNoTracking().FirstOrDefaultAsync(x => x.OrganizationId == organizationId, ct)
        ?? BillingSettings.Defaults(organizationId);

    public async Task SaveAsync(BillingSettings settings, CancellationToken ct = default)
    {
        var existe = await db.BillingSettings.AsNoTracking()
            .AnyAsync(x => x.OrganizationId == settings.OrganizationId, ct);
        if (existe)
        {
            db.BillingSettings.Update(settings);
        }
        else
        {
            db.BillingSettings.Add(settings);
        }
        await db.SaveChangesAsync(ct);
    }
}
