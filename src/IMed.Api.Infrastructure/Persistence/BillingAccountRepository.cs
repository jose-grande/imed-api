using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;
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

    public async Task<IReadOnlyList<AccountSearchRow>> SearchAccountsAsync(
        AccountSearchCriteria criteria, CancellationToken ct = default)
    {
        var c = criteria;
        var org = c.OrganizationId;
        var q = db.BillingAccounts.AsNoTracking().Where(x => x.OrganizationId == org);

        if (!string.IsNullOrWhiteSpace(c.PatientFhirId))
        {
            var p = c.PatientFhirId.Trim();
            q = q.Where(x => x.PatientFhirId == p);
        }
        if (c.Status is { } st)
        {
            q = q.Where(x => x.Status == st);
        }
        if (c.Origin is { } origen)
        {
            q = q.Where(x => x.Origin == origen);
        }
        if (c.OpenedFrom is { } desde)
        {
            q = q.Where(x => x.OpenedAt >= desde);
        }
        if (c.OpenedBefore is { } hasta)
        {
            q = q.Where(x => x.OpenedAt < hasta);
        }
        if (!string.IsNullOrWhiteSpace(c.Text))
        {
            var texto = c.Text.Trim();
            var patron = $"%{EscapeLike(texto)}%";
            q = q.Where(x =>
                x.PatientFhirId == texto
                || (x.OriginRef != null && EF.Functions.ILike(x.OriginRef, patron))
                || db.Parties.Any(p => p.Id == x.PartyId
                                       && p.OrganizationId == org
                                       && (EF.Functions.ILike(p.LegalName, patron)
                                           || (p.TaxId != null && EF.Functions.ILike(p.TaxId, patron)))));
        }
        if (c.LocationId is { } sede)
        {
            q = q.Where(x =>
                db.BillingCharges.Any(ch => ch.OrganizationId == org && ch.AccountId == x.Id && ch.LocationId == sede)
                || db.Invoices.Any(i => i.OrganizationId == org && i.AccountId == x.Id && i.LocationId == sede));
        }

        var filas = q.Select(x => new
        {
            Account = x,
            Uninvoiced = db.BillingCharges.Any(ch =>
                ch.OrganizationId == org && ch.AccountId == x.Id && ch.Status == ChargeStatus.Captured),
            PricePending = db.BillingCharges.Any(ch =>
                ch.OrganizationId == org && ch.AccountId == x.Id && ch.Status == ChargeStatus.Captured && ch.PricePending),
            Live = db.BillingCharges.Any(ch =>
                ch.OrganizationId == org && ch.AccountId == x.Id && ch.Status != ChargeStatus.Reversed),
            Invoiced = db.Invoices.Any(i =>
                i.OrganizationId == org && i.AccountId == x.Id
                && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Voided),
            Balance = db.Invoices
                .Where(i => i.OrganizationId == org && i.AccountId == x.Id
                            && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Voided
                            && i.Balance > 0)
                .Sum(i => (decimal?)i.Balance) ?? 0m,
            OpenInvoices = db.Invoices.Count(i =>
                i.OrganizationId == org && i.AccountId == x.Id
                && i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Voided
                && i.Balance > 0),
        });

        filas = c.Situation switch
        {
            AccountBillingSituation.ToInvoice =>
                filas.Where(f => f.Account.Status != BillingAccountStatus.Voided && f.Uninvoiced),
            AccountBillingSituation.ToCollect =>
                filas.Where(f => f.OpenInvoices > 0),
            AccountBillingSituation.Pending =>
                filas.Where(f => (f.Account.Status != BillingAccountStatus.Voided && f.Uninvoiced) || f.OpenInvoices > 0),
            AccountBillingSituation.Settled =>
                filas.Where(f => f.Account.Status != BillingAccountStatus.Voided
                                 && !f.Uninvoiced && f.OpenInvoices == 0 && f.Invoiced),
            AccountBillingSituation.Empty =>
                filas.Where(f => f.Account.Status != BillingAccountStatus.Voided && !f.Live && !f.Invoiced),
            AccountBillingSituation.Voided =>
                filas.Where(f => f.Account.Status == BillingAccountStatus.Voided),
            _ => filas,
        };

        var lista = await filas
            .OrderByDescending(f => f.Account.OpenedAt)
            .Take(Math.Clamp(c.Limit, 1, 200))
            .ToListAsync(ct);

        return lista
            .Select(f => new AccountSearchRow(
                f.Account, f.Uninvoiced, f.PricePending, f.Live, f.Invoiced, f.Balance, f.OpenInvoices))
            .ToList();
    }

    public async Task<IReadOnlyList<BillingCharge>> ListCapturedChargesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct = default)
    {
        if (accountIds.Count == 0)
        {
            return [];
        }
        return await db.BillingCharges.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && accountIds.Contains(x.AccountId)
                        && x.Status == ChargeStatus.Captured)
            .ToListAsync(ct);
    }

    /// <summary>Lo que el usuario escribe se busca literal: <c>%</c> y <c>_</c> no son comodines.</summary>
    private static string EscapeLike(string texto) =>
        texto.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task AddAccountAsync(BillingAccount account, CancellationToken ct = default)
    {
        db.BillingAccounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> TryAddAccountAsync(BillingAccount account, CancellationToken ct = default)
    {
        db.BillingAccounts.Add(account);
        try
        {
            await db.SaveChangesAsync(ct);
            // Se suelta: quien lo agregó solo necesita el id, y un request que después lo lea y
            // lo actualice por su cuenta chocaría con esta instancia rastreada.
            db.Entry(account).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg
                                           && pg.SqlState == UniqueViolation)
        {
            db.Entry(account).State = EntityState.Detached;
            return false;
        }
    }

    public Task<BillingAccount?> FindByOriginAsync(
        Guid organizationId, BillingAccountOrigin origin, string originRef, CancellationToken ct = default) =>
        db.BillingAccounts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.Origin == origin
                                      && x.OriginRef == originRef, ct);

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

    public async Task<IReadOnlyList<BillingCharge>> ListPricePendingChargesForUpdateAsync(
        Guid organizationId, Guid billableItemId, CancellationToken ct = default) =>
        await db.BillingCharges
            .Where(x => x.OrganizationId == organizationId
                        && x.BillableItemId == billableItemId
                        && x.Status == ChargeStatus.Captured
                        && x.PricePending)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BillingCharge>> ListLiveChargesBySourceForUpdateAsync(
        Guid organizationId, ChargeSourceKind sourceKind, string sourceId, CancellationToken ct = default) =>
        await db.BillingCharges
            .Where(x => x.OrganizationId == organizationId
                        && x.SourceKind == sourceKind
                        && x.SourceId == sourceId
                        && x.Status != ChargeStatus.Reversed)
            .ToListAsync(ct);

    public Task<bool> HasConsultationSinceAsync(
        Guid organizationId, string patientFhirId, string specialtyCode, DateTimeOffset since,
        string? excludeEncounterFhirId, CancellationToken ct = default) =>
        (from c in db.BillingCharges
         join a in db.BillingAccounts on c.AccountId equals a.Id
         join i in db.BillableItems on c.BillableItemId equals i.Id
         where c.OrganizationId == organizationId
               && c.SourceKind == ChargeSourceKind.Encounter
               && c.Status != ChargeStatus.Reversed
               && c.PerformedAt >= since
               && a.PatientFhirId == patientFhirId
               && i.SpecialtyCode == specialtyCode
               && (excludeEncounterFhirId == null || c.SourceId != excludeEncounterFhirId)
         select c.Id).AnyAsync(ct);

    public async Task<IReadOnlyDictionary<string, decimal?>> PrepaidAppointmentBalancesAsync(
        Guid organizationId, IReadOnlyCollection<string> appointmentIds, CancellationToken ct = default)
    {
        if (appointmentIds.Count == 0)
        {
            return new Dictionary<string, decimal?>();
        }
        var ids = appointmentIds.Select(a => a.Trim()).Distinct().ToList();
        var filas = await (
            from c in db.BillingCharges.AsNoTracking()
            where c.OrganizationId == organizationId
                  && c.SourceKind == ChargeSourceKind.Appointment
                  && c.Status != ChargeStatus.Reversed
                  && ids.Contains(c.SourceId)
            join l in db.InvoiceLines.AsNoTracking().Where(l => !l.Voided) on c.Id equals l.ChargeId into ls
            from l in ls.DefaultIfEmpty()
            join f in db.Invoices.AsNoTracking() on l.InvoiceId equals f.Id into fs
            from f in fs.DefaultIfEmpty()
            select new { c.SourceId, Balance = f == null ? (decimal?)null : f.Balance })
            .ToListAsync(ct);
        // Si por algún motivo hubiera dos filas para la misma cita, manda la que más debe.
        return filas.GroupBy(x => x.SourceId)
            .ToDictionary(g => g.Key, g => g.Any(x => x.Balance is null) ? null : g.Max(x => x.Balance));
    }
}
