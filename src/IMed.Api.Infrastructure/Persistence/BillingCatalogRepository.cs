using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Catalog;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Catálogo de cobrables y tarifarios sobre EF Core (FAC-3). Lecturas estrictas por org.</summary>
public sealed class BillingCatalogRepository(AppDbContext db) : IBillingCatalogRepository
{
    public async Task<IReadOnlyList<BillableItem>> SearchItemsAsync(
        Guid organizationId, string? term, bool activeOnly, int limit, CancellationToken ct = default)
    {
        var q = db.BillableItems.AsNoTracking().Where(x => x.OrganizationId == organizationId);

        if (activeOnly)
        {
            q = q.Where(x => x.Active);
        }
        if (!string.IsNullOrWhiteSpace(term))
        {
            // Por código con prefijo y por nombre en cualquier parte: la caja teclea el código
            // cuando lo sabe y el nombre cuando no, y buscar el código por dentro devolvería ruido.
            var t = term.Trim();
            var prefijo = $"{t}%";
            var contiene = $"%{t}%";
            q = q.Where(x =>
                EF.Functions.ILike(x.Code, prefijo) ||
                EF.Functions.ILike(EF.Functions.Unaccent(x.Name), EF.Functions.Unaccent(contiene)));
        }

        return await q.OrderBy(x => x.Name).Take(limit).ToListAsync(ct);
    }

    public Task<BillableItem?> GetItemAsync(Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.BillableItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public Task<bool> ItemCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.BillableItems.AnyAsync(x => x.OrganizationId == organizationId && x.Code == c, ct);
    }

    public async Task AddItemAsync(BillableItem item, CancellationToken ct = default)
    {
        db.BillableItems.Add(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateItemAsync(BillableItem item, CancellationToken ct = default)
    {
        db.BillableItems.Update(item);
        await db.SaveChangesAsync(ct);
    }

    public Task<BillableItem?> FindByLabAsync(
        Guid organizationId, Guid? labTestId, Guid? labPanelId, CancellationToken ct = default)
    {
        if (labTestId is null && labPanelId is null)
        {
            return Task.FromResult<BillableItem?>(null);
        }
        return db.BillableItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Active)
            .Where(x => (labTestId != null && x.LabTestId == labTestId)
                        || (labPanelId != null && x.LabPanelId == labPanelId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<BillableItem>> ListPricePendingItemsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.BillableItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Active && x.PricePending)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);

    public Task<BillableItem?> FindBySpecialtyAsync(
        Guid organizationId, string specialtyCode, ConsultationType type, CancellationToken ct = default) =>
        db.BillableItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                      && x.SpecialtyCode == specialtyCode
                                      && x.ConsultationType == type, ct);

    public Task<BillableItem?> FindAnyByLabAsync(
        Guid organizationId, Guid? labTestId, Guid? labPanelId, CancellationToken ct = default)
    {
        if (labTestId is null && labPanelId is null)
        {
            return Task.FromResult<BillableItem?>(null);
        }
        return db.BillableItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Where(x => (labTestId != null && x.LabTestId == labTestId)
                        || (labPanelId != null && x.LabPanelId == labPanelId))
            .FirstOrDefaultAsync(ct);
    }

    public Task<BillableItem?> FindAnyByStockItemAsync(
        Guid organizationId, Guid stockItemId, CancellationToken ct = default) =>
        db.BillableItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.StockItemId == stockItemId, ct);

    public async Task<bool> TryAddLinkedItemAsync(BillableItem item, CancellationToken ct = default)
    {
        db.BillableItems.Add(item);
        try
        {
            await db.SaveChangesAsync(ct);
            // Se suelta: quien lo agregó solo necesita el id, y un request que después lo lea y
            // lo actualice por su cuenta chocaría con esta instancia rastreada.
            db.Entry(item).State = EntityState.Detached;
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Otro camino dio de alta el mismo enlace entre la lectura y esta escritura (o el
            // código chocó con uno existente): el índice único lo impidió y el resultado es el mismo.
            db.Entry(item).State = EntityState.Detached;
            return false;
        }
    }

    // ---- Tarifarios ----

    public async Task<IReadOnlyList<PriceList>> ListPriceListsAsync(
        Guid organizationId, CancellationToken ct = default) =>
        await db.PriceLists.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.Kind).ThenByDescending(x => x.ValidFrom)
            .ToListAsync(ct);

    public Task<PriceList?> GetPriceListAsync(Guid organizationId, Guid id, CancellationToken ct = default) =>
        db.PriceLists.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == id, ct);

    public Task<bool> PriceListCodeExistsAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.PriceLists.AnyAsync(x => x.OrganizationId == organizationId && x.Code == c, ct);
    }

    public async Task AddPriceListAsync(PriceList list, CancellationToken ct = default)
    {
        db.PriceLists.Add(list);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdatePriceListAsync(PriceList list, CancellationToken ct = default)
    {
        db.PriceLists.Update(list);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PriceListItem>> ListPriceListItemsAsync(
        Guid organizationId, Guid priceListId, CancellationToken ct = default) =>
        await db.PriceListItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.PriceListId == priceListId)
            .OrderByDescending(x => x.ValidFrom)
            .ToListAsync(ct);

    public async Task AddPriceListItemAsync(PriceListItem item, CancellationToken ct = default)
    {
        db.PriceListItems.Add(item);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PriceCandidate>> GetPriceCandidatesAsync(
        Guid organizationId, Guid billableItemId, CancellationToken ct = default)
    {
        var rows = await db.PriceListItems.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.BillableItemId == billableItemId)
            .Join(
                db.PriceLists.AsNoTracking().Where(l => l.OrganizationId == organizationId),
                item => item.PriceListId,
                list => list.Id,
                (item, list) => new { item, list })
            .ToListAsync(ct);

        return rows.Select(r => new PriceCandidate(r.list, r.item)).ToList();
    }
}
