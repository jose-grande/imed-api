using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Motor fiscal sobre EF Core (FAC-2). Lecturas estrictas por organización, como el resto.
/// </summary>
public sealed class FiscalRepository(AppDbContext db) : IFiscalRepository
{
    // ---- Régimen y catálogos ------------------------------------------------

    public async Task<FiscalRegime?> GetActiveRegimeAsync(
        Guid organizationId, DateOnly day, CancellationToken ct = default)
    {
        var rows = await db.FiscalRegimes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ValidFrom <= day)
            .OrderByDescending(x => x.ValidFrom)
            .ToListAsync(ct);
        return rows.FirstOrDefault(x => x.IsInEffectOn(day));
    }

    public async Task AddRegimeAsync(FiscalRegime regime, CancellationToken ct = default)
    {
        db.FiscalRegimes.Add(regime);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateRegimeAsync(FiscalRegime regime, CancellationToken ct = default)
    {
        if (db.Entry(regime).State == EntityState.Detached)
        {
            db.FiscalRegimes.Update(regime);
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TaxCode>> ListTaxCodesAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.TaxCodes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

    public Task<TaxCode?> GetTaxCodeAsync(Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.TaxCodes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public async Task AddTaxCodeAsync(TaxCode taxCode, CancellationToken ct = default)
    {
        db.TaxCodes.Add(taxCode);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FiscalDocumentType>> ListDocumentTypesAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.FiscalDocumentTypes.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.DocumentCode)
            .ToListAsync(ct);

    public Task<FiscalDocumentType?> GetDocumentTypeAsync(
        Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.FiscalDocumentTypes.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public async Task AddDocumentTypeAsync(FiscalDocumentType type, CancellationToken ct = default)
    {
        db.FiscalDocumentTypes.Add(type);
        await db.SaveChangesAsync(ct);
    }

    // ---- Autorizaciones -----------------------------------------------------

    public async Task<IReadOnlyList<FiscalAuthorization>> ListAuthorizationsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.FiscalAuthorizations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.IssuedAt)
            .ToListAsync(ct);

    public Task<FiscalAuthorization?> GetAuthorizationAsync(
        Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.FiscalAuthorizations.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public async Task AddAuthorizationAsync(FiscalAuthorization authorization, CancellationToken ct = default)
    {
        db.FiscalAuthorizations.Add(authorization);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAuthorizationAsync(FiscalAuthorization authorization, CancellationToken ct = default)
    {
        db.FiscalAuthorizations.Update(authorization);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<FiscalReservation> ReserveNextSequenceAsync(
        Guid organizationId, Guid authorizationId, DateOnly today, CancellationToken ct = default)
    {
        // Una sola sentencia: verifica y consume. El UPDATE toma el lock de la fila, así que dos
        // cajas concurrentes se serializan acá y cada una se lleva un número distinto — nunca el
        // mismo, y nunca uno fuera de rango o de fecha.
        //
        // `RETURNING next_sequence - 1` porque RETURNING entrega los valores YA actualizados:
        // el número que le toca a quien llamó es el anterior al nuevo.
        //
        // El CASE deja la autorización marcada como agotada en el mismo golpe. Sin eso, la fila
        // seguiría diciendo "activa" hasta que alguien intentara emitir, y la pantalla que avisa
        // "quedan N documentos" mentiría justo cuando importa.
        var reservados = await db.Database
            .SqlQuery<long>($"""
                UPDATE fiscal_authorization
                SET next_sequence = next_sequence + 1,
                    status = CASE
                        WHEN next_sequence + 1 > range_to THEN 'Exhausted'
                        ELSE status
                    END,
                    updated_at = now()
                WHERE id = {authorizationId}
                  AND organization_id = {organizationId}
                  AND status = 'Active'
                  AND next_sequence <= range_to
                  AND limit_date >= {today}
                RETURNING next_sequence - 1 AS "Value"
                """)
            .ToListAsync(ct);

        if (reservados.Count > 0)
        {
            return FiscalReservation.Ok(reservados[0]);
        }

        // No se reservó nada. Averiguar por qué es el camino de error, no el caliente, así que una
        // segunda consulta acá no cuesta: lo que importa es que el usuario sepa si tiene que pedir
        // un rango nuevo, renovar la fecha o hablar con administración.
        var auth = await GetAuthorizationAsync(organizationId, authorizationId, ct);
        if (auth is null)
        {
            return FiscalReservation.Failed(FiscalReservationFailure.NotFound);
        }
        return FiscalReservation.Failed(
            auth.BlockedReason(today) ?? FiscalReservationFailure.Exhausted);
    }

    // ---- Puntos de cobro ----------------------------------------------------

    public async Task<IReadOnlyList<CashPoint>> ListCashPointsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.CashPoints.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Code)
            .ToListAsync(ct);

    public Task<CashPoint?> GetCashPointAsync(Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.CashPoints.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public Task<bool> CashPointCodeExistsAsync(
        Guid organizationId, string code, CancellationToken ct = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.CashPoints.AnyAsync(x => x.OrganizationId == organizationId && x.Code == c, ct);
    }

    public async Task AddCashPointAsync(CashPoint cashPoint, CancellationToken ct = default)
    {
        db.CashPoints.Add(cashPoint);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateCashPointAsync(CashPoint cashPoint, CancellationToken ct = default)
    {
        db.CashPoints.Update(cashPoint);
        await db.SaveChangesAsync(ct);
    }
}
