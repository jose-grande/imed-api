using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Partes sobre EF Core (FAC-1). Lecturas estrictas por organización.</summary>
public sealed class PartyRepository(AppDbContext db) : IPartyRepository
{
    public Task<Party?> GetAsync(Guid organizationId, Guid partyId, CancellationToken ct = default) =>
        db.Parties.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == partyId, ct);

    public Task<Party?> FindByPatientAsync(
        Guid organizationId, string patientFhirId, CancellationToken ct = default)
    {
        var p = patientFhirId.Trim();
        return db.Parties.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.PatientFhirId == p, ct);
    }

    public async Task<IReadOnlyList<Party>> SearchAsync(
        Guid organizationId, string? term, int limit, CancellationToken ct = default)
    {
        var q = db.Parties.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.Active);

        if (!string.IsNullOrWhiteSpace(term))
        {
            // Por nombre en cualquier parte y por RTN exacto: la caja teclea el RTN completo
            // cuando lo tiene, y buscar por dentro de un RTN devolvería ruido.
            var t = term.Trim();
            var contiene = $"%{t}%";
            q = q.Where(x =>
                EF.Functions.ILike(EF.Functions.Unaccent(x.LegalName), EF.Functions.Unaccent(contiene))
                || x.TaxId == t);
        }

        return await q.OrderBy(x => x.LegalName).Take(Math.Clamp(limit, 1, 200)).ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> partyIds, CancellationToken ct = default)
    {
        if (partyIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        return await db.Parties.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && partyIds.Contains(x.Id))
            .Select(x => new { x.Id, x.LegalName })
            .ToDictionaryAsync(x => x.Id, x => x.LegalName, ct);
    }

    public async Task<bool> TryAddAsync(Party party, CancellationToken ct = default)
    {
        db.Parties.Add(party);
        try
        {
            await db.SaveChangesAsync(ct);
            // Se suelta: quien lo agregó solo necesita el id, y un request que después lo lea y
            // lo actualice por su cuenta chocaría con esta instancia rastreada.
            db.Entry(party).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            db.Entry(party).State = EntityState.Detached;
            return false;
        }
    }

    public async Task AddAsync(Party party, CancellationToken ct = default)
    {
        db.Parties.Add(party);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(Party party, CancellationToken ct = default)
    {
        db.Parties.Update(party);
        await db.SaveChangesAsync(ct);
    }
}
