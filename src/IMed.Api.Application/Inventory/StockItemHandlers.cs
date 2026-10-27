using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Inventory;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Inventory;

public sealed record PresentationInput(string Name, decimal FactorToBase);

/// <summary>Datos de un artículo, para crearlo o editarlo. El código y el tipo solo se usan al crear.</summary>
public sealed record StockItemInput(
    string Code,
    string Name,
    string Kind,
    string BaseUnit,
    bool RequiresLot,
    string? Barcode,
    bool Sellable,
    bool OverTheCounter,
    Guid? TaxCodeId,
    decimal? MinimumStock,
    bool Active,
    string? AtcCode,
    string? ActiveIngredient,
    string? Concentration,
    string? DoseForm,
    string? Route,
    string? SanitaryRegistration,
    string? StorageConditions,
    int? OpenStabilityDays,
    bool? Sterile,
    bool? SingleUse,
    IReadOnlyList<PresentationInput>? Presentations);

/// <summary>
/// Alta y edición de artículos (B1) con su cobrable automático (B2).
///
/// <para><b>Vendible ⇒ cobrable.</b> Al marcar un artículo como vendible se asegura su renglón en
/// el catálogo de facturación "sin precio" (D1, D12): la farmacia puede despacharlo el mismo día y
/// la factura espera a que el jefe administrativo fije el precio. El alta es idempotente, así que
/// guardar dos veces no crea dos cobrables.</para>
/// </summary>
public sealed class StockItemCommandHandler(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IFiscalRepository fiscal,
    IBillableItemProvisioner provisioner,
    IBillingCatalogRepository catalog,
    ILogger<StockItemCommandHandler> logger)
{
    public async Task<StockItemDto> CreateAsync(Guid organizationId, StockItemInput input, CancellationToken ct = default)
    {
        if (!Enum.TryParse<StockItemKind>(input.Kind, ignoreCase: true, out var tipo))
        {
            throw new ArgumentException("El tipo de artículo es medicamento, reactivo o insumo.", nameof(input));
        }
        var codigo = (input.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (await repo.ItemCodeExistsAsync(organizationId, codigo, ct))
        {
            throw new ArgumentException($"Ya existe un artículo con el código {codigo}.", nameof(input));
        }

        var item = StockItem.Create(organizationId, codigo, input.Name, tipo, input.BaseUnit, input.RequiresLot, input.Barcode);
        await AplicarAsync(organizationId, item, input, esNuevo: true, ct);
        await repo.AddItemAsync(item, ct);
        await uow.SaveChangesAsync(ct);

        await AsegurarCobrableAsync(organizationId, item, ct);
        return await DetalleAsync(organizationId, item, ct);
    }

    public async Task<StockItemDto?> UpdateAsync(
        Guid organizationId, Guid itemId, StockItemInput input, CancellationToken ct = default)
    {
        var item = await repo.GetItemAsync(organizationId, itemId, ct);
        if (item is null)
        {
            return null;
        }
        item.Rename(input.Name, input.BaseUnit, input.Barcode, input.RequiresLot);
        await AplicarAsync(organizationId, item, input, esNuevo: false, ct);
        item.SetActive(input.Active);
        await repo.UpdateItemAsync(item, ct);
        await uow.SaveChangesAsync(ct);

        await AsegurarCobrableAsync(organizationId, item, ct);
        return await DetalleAsync(organizationId, item, ct);
    }

    public async Task<StockItemDto?> GetAsync(Guid organizationId, Guid itemId, CancellationToken ct = default)
    {
        var item = await repo.GetItemAsync(organizationId, itemId, ct);
        return item is null ? null : await DetalleAsync(organizationId, item, ct);
    }

    /// <summary>
    /// Asegura el cobrable de un artículo vendible que todavía no lo tiene: la organización no tenía
    /// régimen fiscal cuando se creó, o el alta falló. Lo llaman la edición, el despacho, la venta
    /// y el backfill de arranque. Devuelve el id del cobrable, o null si no se puede todavía.
    /// </summary>
    public async Task<Guid?> AsegurarCobrableAsync(Guid organizationId, StockItem item, CancellationToken ct = default)
    {
        if (!item.Sellable || item.TaxCodeId is not { } tributo)
        {
            return item.BillableItemId;
        }
        if (item.BillableItemId is { } existente)
        {
            return existente;
        }
        var cobrable = await provisioner.EnsureForStockItemAsync(organizationId, item.Id, item.Code, item.Name, tributo, ct);
        if (cobrable is null)
        {
            logger.LogInformation("Artículo {ItemId} vendible sin cobrable todavía (org {OrgId}).", item.Id, organizationId);
            return null;
        }
        item.LinkBillableItem(cobrable.Id);
        await repo.UpdateItemAsync(item, ct);
        await uow.SaveChangesAsync(ct);
        return cobrable.Id;
    }

    private async Task AplicarAsync(
        Guid organizationId, StockItem item, StockItemInput input, bool esNuevo, CancellationToken ct)
    {
        switch (item.Kind)
        {
            case StockItemKind.Medication:
                item.SetMedicationProfile(MedicationProfile.Create(
                    input.AtcCode ?? string.Empty, input.ActiveIngredient, input.Concentration, input.DoseForm,
                    input.Route, input.SanitaryRegistration));
                break;
            case StockItemKind.Reagent:
                item.SetReagentProfile(ReagentProfile.Create(input.StorageConditions, input.OpenStabilityDays));
                break;
            case StockItemKind.Supply:
                item.SetSupplyProfile(SupplyProfile.Create(input.Sterile ?? false, input.SingleUse ?? false));
                break;
        }

        if (input.TaxCodeId is { } tributo && await fiscal.GetTaxCodeAsync(organizationId, tributo, ct) is null)
        {
            throw new ArgumentException("El tributo no existe en esta organización.", nameof(input));
        }

        // El tributo de un artículo ya enlazado vive en el catálogo de cobrables: cambiarlo aquí
        // dejaría al artículo diciendo una cosa y a la factura cobrando otra.
        if (!esNuevo && item.BillableItemId is not null && input.Sellable && item.TaxCodeId != input.TaxCodeId)
        {
            throw new ArgumentException(
                "Este artículo ya tiene su cobrable: el tributo se cambia en Facturación → Catálogo de cobrables.",
                nameof(input));
        }

        item.SetSale(input.Sellable, input.OverTheCounter, input.Sellable ? input.TaxCodeId : null);
        item.SetMinimumStock(input.MinimumStock);
        item.ReplacePresentations((input.Presentations ?? []).Select(p => (p.Name, p.FactorToBase)));
    }

    private async Task<StockItemDto> DetalleAsync(Guid organizationId, StockItem item, CancellationToken ct)
    {
        if (item.BillableItemId is { } cobrableId
            && await catalog.GetItemAsync(organizationId, cobrableId, ct) is { } cobrable)
        {
            return StockItemMapper.ToDto(item, cobrable.DefaultPrice, cobrable.PricePending);
        }
        return StockItemMapper.ToDto(item);
    }
}

public sealed class SearchStockItemsHandler(IInventoryRepository repo)
{
    public async Task<IReadOnlyList<StockItemDto>> HandleAsync(
        Guid organizationId, string? term, string? kind, bool? overTheCounter, bool activeOnly, int limit,
        CancellationToken ct = default)
    {
        StockItemKind? tipo = Enum.TryParse<StockItemKind>(kind, ignoreCase: true, out var k) ? k : null;
        var items = await repo.SearchItemsAsync(organizationId, term, tipo, overTheCounter, activeOnly, Math.Clamp(limit, 1, 500), ct);
        return items.Select(i => StockItemMapper.ToDto(i)).ToList();
    }
}
