using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class PaymentTerminalRepository(AppDbContext db) : IPaymentTerminalRepository
{
    public async Task<IReadOnlyList<PaymentTerminal>> ListAsync(
        Guid organizationId, Guid? locationId = null, CancellationToken ct = default) =>
        await db.PaymentTerminals.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && (locationId == null || x.LocationId == locationId))
            .OrderBy(x => x.Kind).ThenBy(x => x.Name)
            .ToListAsync(ct);

    public Task<PaymentTerminal?> GetAsync(Guid organizationId, Guid terminalId, CancellationToken ct = default) =>
        db.PaymentTerminals.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Id == terminalId, ct);

    public async Task AddAsync(PaymentTerminal terminal, CancellationToken ct = default)
    {
        db.PaymentTerminals.Add(terminal);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(PaymentTerminal terminal, CancellationToken ct = default)
    {
        if (db.Entry(terminal).State == EntityState.Detached)
        {
            db.PaymentTerminals.Update(terminal);
        }
        await db.SaveChangesAsync(ct);
    }
}
