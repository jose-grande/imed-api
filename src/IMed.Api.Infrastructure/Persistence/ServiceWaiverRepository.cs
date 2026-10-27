using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Gate;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Constancias de liberación sobre EF Core (FAC-7).</summary>
public sealed class ServiceWaiverRepository(AppDbContext db, IClinicClock clock) : IServiceWaiverRepository
{
    public Task<ServiceWaiver?> FindAsync(
        Guid organizationId, string sourceRef, CancellationToken ct = default)
    {
        var r = sourceRef.Trim();
        return db.ServiceWaivers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.SourceRef == r, ct);
    }

    public async Task<IReadOnlyList<ServiceWaiver>> ListAsync(
        Guid organizationId, DateOnly? since, DateOnly? until, CancellationToken ct = default)
    {
        var q = db.ServiceWaivers.AsNoTracking().Where(x => x.OrganizationId == organizationId);

        if (since is { } desde)
        {
            var inicio = clock.StartOf(desde);
            q = q.Where(x => x.WaivedAt >= inicio);
        }
        if (until is { } hasta)
        {
            // Inclusivo: quien pide "hasta el 25" espera que el 25 esté adentro.
            var fin = clock.StartOf(hasta.AddDays(1));
            q = q.Where(x => x.WaivedAt < fin);
        }

        return await q.OrderByDescending(x => x.WaivedAt).Take(500).ToListAsync(ct);
    }

    public async Task AddAsync(ServiceWaiver waiver, CancellationToken ct = default)
    {
        db.ServiceWaivers.Add(waiver);
        await db.SaveChangesAsync(ct);
    }
}
