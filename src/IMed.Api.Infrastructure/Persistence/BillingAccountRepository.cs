using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Cuentas y cargos sobre EF Core (FAC-4). Lecturas estrictas por organización.</summary>
public sealed class BillingAccountRepository(AppDbContext db) : IBillingAccountRepository
{
    /// <summary>Código de Postgres para violación de índice único.</summary>
    private const string UniqueViolation = "23505";

    public Task<BillingAccount?> GetAccountAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default) =>
        db.BillingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == accountId, ct);

    public Task<BillingAccount?> FindByEncounterAsync(
        Guid organizationId, string encounterFhirId, CancellationToken ct = default)
    {
        var enc = encounterFhirId.Trim();
        return db.BillingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.EncounterFhirId == enc, ct);
    }

    public async Task<IReadOnlyList<BillingAccount>> SearchAccountsAsync(
        Guid organizationId, string? patientFhirId, string? status, int limit, CancellationToken ct = default)
    {
        var q = db.BillingAccounts.AsNoTracking().Where(x => x.OrganizationId == organizationId);

        if (!string.IsNullOrWhiteSpace(patientFhirId))
        {
            var p = patientFhirId.Trim();
            q = q.Where(x => x.PatientFhirId == p);
        }
        if (Enum.TryParse<BillingAccountStatus>(status, ignoreCase: true, out var st))
        {
            q = q.Where(x => x.Status == st);
        }

        return await q.OrderByDescending(x => x.OpenedAt).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
    }

    public async Task AddAccountAsync(BillingAccount account, CancellationToken ct = default)
    {
        db.BillingAccounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAccountAsync(BillingAccount account, CancellationToken ct = default)
    {
        db.BillingAccounts.Update(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<BillingCharge>> ListChargesAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default) =>
        await db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.AccountId == accountId)
            .OrderBy(x => x.PerformedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BillingCharge>> ListCapturedChargesForUpdateAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default) =>
        await db.BillingCharges
            .Where(x => x.OrganizationId == organizationId
                        && x.AccountId == accountId
                        && x.Status == ChargeStatus.Captured)
            .OrderBy(x => x.PerformedAt)
            .ToListAsync(ct);

    public Task<BillingCharge?> GetChargeAsync(
        Guid organizationId, Guid chargeId, CancellationToken ct = default) =>
        db.BillingCharges.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == chargeId, ct);

    public Task<BillingCharge?> GetChargeForUpdateAsync(
        Guid organizationId, Guid chargeId, CancellationToken ct = default) =>
        db.BillingCharges
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == chargeId, ct);

    public Task<BillingCharge?> FindAnyLiveChargeBySourceAsync(
        Guid organizationId,
        ChargeSourceKind sourceKind,
        string sourceId,
        CancellationToken ct = default)
    {
        var sid = sourceId.Trim();
        return db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.SourceKind == sourceKind
                        && x.SourceId == sid
                        && x.Status != ChargeStatus.Reversed)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    public Task<BillingCharge?> FindLiveChargeBySourceAsync(
        Guid organizationId,
        ChargeSourceKind sourceKind,
        string sourceId,
        Guid billableItemId,
        CancellationToken ct = default)
    {
        var sid = sourceId.Trim();
        return db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.SourceKind == sourceKind
                        && x.SourceId == sid
                        && x.BillableItemId == billableItemId
                        && x.Status != ChargeStatus.Reversed)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryAddChargeAsync(BillingCharge charge, CancellationToken ct = default)
    {
        db.BillingCharges.Add(charge);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
                                           && pg.SqlState == UniqueViolation)
        {
            // Otro camino cobró el mismo acto entre la lectura y esta escritura. No es un error:
            // es exactamente lo que el índice existe para impedir, y el resultado correcto es
            // "ya estaba cobrado". Comprobar antes de insertar no alcanza —dos hilos pueden pasar
            // la comprobación a la vez—, así que la base es la que decide.
            db.Entry(charge).State = EntityState.Detached;
            return false;
        }
    }

    public async Task UpdateChargeAsync(BillingCharge charge, CancellationToken ct = default)
    {
        // Si vino de `GetChargeForUpdateAsync` ya está rastreada y basta con guardar; pedir
        // `Update` sobre una instancia distinta de la misma fila es lo que produce el conflicto
        // de identidad.
        if (db.Entry(charge).State == EntityState.Detached)
        {
            db.BillingCharges.Update(charge);
        }
        await db.SaveChangesAsync(ct);
    }
}
