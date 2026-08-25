using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Lecturas de reporte sobre EF Core (FAC-8). Todas estrictas por organización.</summary>
public sealed class FiscalReportRepository(AppDbContext db) : IFiscalReportRepository
{
    public async Task<IReadOnlyList<FiscalDocument>> ListDocumentsAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default)
    {
        var (desde, hasta) = Rango(since, until);
        return await db.FiscalDocuments.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.IssuedAt >= desde && x.IssuedAt < hasta)
            .OrderBy(x => x.IssuedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FiscalDocument>> ListByAuthorizationAsync(
        Guid organizationId, Guid authorizationId, CancellationToken ct = default) =>
        await db.FiscalDocuments.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.FiscalAuthorizationId == authorizationId)
            .OrderBy(x => x.Sequence)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BillingCharge>> ListManualChargesAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default)
    {
        var (desde, hasta) = Rango(since, until);
        return await db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.SourceKind == ChargeSourceKind.Manual
                        && x.Status != ChargeStatus.Reversed
                        && x.CreatedAt >= desde && x.CreatedAt < hasta)
            .OrderByDescending(x => x.CreatedAt)
            .Take(1000)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BillingCharge>> ListAuthorizedDiscountsAsync(
        Guid organizationId, DateOnly since, DateOnly until, CancellationToken ct = default)
    {
        var (desde, hasta) = Rango(since, until);
        return await db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.DiscountAuthorizedBy != null
                        && x.Status != ChargeStatus.Reversed
                        && x.CreatedAt >= desde && x.CreatedAt < hasta)
            .OrderByDescending(x => x.CreatedAt)
            .Take(1000)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Rango medio abierto <c>[desde, hasta)</c>, con el día final incluido: quien pide "del 1 al
    /// 25" espera que lo del 25 a las 18:00 aparezca.
    /// </summary>
    private static (DateTimeOffset Desde, DateTimeOffset Hasta) Rango(DateOnly since, DateOnly until) =>
        (new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
         new DateTimeOffset(until.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
}
