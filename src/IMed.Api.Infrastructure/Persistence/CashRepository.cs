using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Caja sobre EF Core (FAC-6). Lecturas estrictas por organización.</summary>
public sealed class CashRepository(AppDbContext db) : ICashRepository
{
    public Task<CashSession?> GetSessionAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default) =>
        db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == sessionId, ct);

    public Task<CashSession?> GetSessionForUpdateAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default) =>
        db.CashSessions
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == sessionId, ct);

    public Task<CashSession?> FindOpenSessionByPointAsync(
        Guid organizationId, Guid cashPointId, CancellationToken ct = default) =>
        db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.CashPointId == cashPointId
                     && x.Status == CashSessionStatus.Open,
                ct);

    public Task<CashSession?> FindOpenSessionByCashierAsync(
        Guid organizationId, Guid cashierUserId, CancellationToken ct = default) =>
        db.CashSessions.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.OrganizationId == organizationId
                     && x.CashierUserId == cashierUserId
                     && x.Status == CashSessionStatus.Open,
                ct);

    public async Task<IReadOnlyList<CashSession>> ListSessionsAsync(
        Guid organizationId, Guid? cashPointId, DateOnly? day, CancellationToken ct = default)
    {
        var q = db.CashSessions.AsNoTracking().Where(x => x.OrganizationId == organizationId);

        if (cashPointId is { } point)
        {
            q = q.Where(x => x.CashPointId == point);
        }
        if (day is { } d)
        {
            var desde = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var hasta = desde.AddDays(1);
            q = q.Where(x => x.OpenedAt >= desde && x.OpenedAt < hasta);
        }

        return await q.OrderByDescending(x => x.OpenedAt).Take(200).ToListAsync(ct);
    }

    public async Task AddSessionAsync(CashSession session, CancellationToken ct = default)
    {
        db.CashSessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateSessionAsync(CashSession session, CancellationToken ct = default)
    {
        // Si vino de `GetSessionForUpdateAsync` ya está rastreada y basta con guardar.
        if (db.Entry(session).State == EntityState.Detached)
        {
            db.CashSessions.Update(session);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<CashMovement>> ListMovementsAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default) =>
        await db.CashMovements.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.CashSessionId == sessionId)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync(ct);

    public async Task AddMovementAsync(CashMovement movement, CancellationToken ct = default)
    {
        db.CashMovements.Add(movement);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Payment>> ListPaymentsAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default) =>
        await db.Payments.AsNoTracking()
            .Include(x => x.Allocations)
            .Where(x => x.OrganizationId == organizationId && x.CashSessionId == sessionId)
            .OrderBy(x => x.ReceivedAt)
            .ToListAsync(ct);

    public Task<Payment?> GetPaymentAsync(
        Guid organizationId, Guid paymentId, CancellationToken ct = default) =>
        db.Payments.AsNoTracking()
            .Include(x => x.Allocations)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == paymentId, ct);

    /// <inheritdoc />
    public async Task SavePaymentAsync(
        Payment payment, IReadOnlyList<Invoice> settledInvoices, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        db.Payments.Add(payment);

        // Las facturas vienen rastreadas del handler; solo se adjuntan las que llegan sueltas.
        foreach (var f in settledInvoices.Where(f => db.Entry(f).State == EntityState.Detached))
        {
            db.Invoices.Attach(f);
            db.Entry(f).Property(x => x.Balance).IsModified = true;
            db.Entry(f).Property(x => x.Status).IsModified = true;
            db.Entry(f).Property(x => x.UpdatedAt).IsModified = true;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
