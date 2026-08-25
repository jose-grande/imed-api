using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Application.Billing;

public sealed record BillableItemDto(
    Guid Id,
    string Code,
    string Name,
    string Kind,
    string Component,
    Guid TaxCodeId,
    decimal DefaultPrice,
    string PaymentTiming,
    Guid? LabTestId,
    Guid? LabPanelId,
    string? ServiceCode,
    bool Active);

/// <summary>Un cobrable con su precio ya resuelto y la razón. Es lo que consume la caja.</summary>
public sealed record PricedItemDto(
    BillableItemDto Item,
    decimal Price,
    string PriceSource,
    Guid? PriceListId,
    string? PriceListName,
    decimal? MinPrice,
    decimal? DiscountPctMax,
    string Explanation);

public static class BillableItemMapper
{
    public static BillableItemDto ToDto(BillableItem i) =>
        new(i.Id, i.Code, i.Name, i.Kind.ToString(), i.Component.ToString(), i.TaxCodeId,
            i.DefaultPrice, i.PaymentTiming.ToString(), i.LabTestId, i.LabPanelId,
            i.ServiceCode, i.Active);
}

public sealed class SearchBillableItemsHandler(IBillingCatalogRepository repo)
{
    public async Task<IReadOnlyList<BillableItemDto>> HandleAsync(
        Guid orgId, string? term, bool activeOnly, int limit, CancellationToken ct = default)
    {
        var rows = await repo.SearchItemsAsync(orgId, term, activeOnly, Math.Clamp(limit, 1, 200), ct);
        return rows.Select(BillableItemMapper.ToDto).ToList();
    }
}

public sealed record CreateBillableItemCommand(
    Guid OrganizationId,
    string Code,
    string Name,
    string Kind,
    Guid TaxCodeId,
    decimal DefaultPrice,
    string? PaymentTiming,
    string? Component,
    Guid? LabTestId,
    Guid? LabPanelId,
    string? ServiceCode);

public sealed class CreateBillableItemHandler(
    IBillingCatalogRepository repo, IFiscalRepository fiscal)
{
    public async Task<BillableItemDto> HandleAsync(
        CreateBillableItemCommand cmd, CancellationToken ct = default)
    {
        if (await repo.ItemCodeExistsAsync(cmd.OrganizationId, cmd.Code, ct))
        {
            throw new InvalidOperationException($"Ya existe un cobrable con el código '{cmd.Code}'.");
        }

        // El tributo tiene que existir en esta organización. Sin esta verificación, un id
        // equivocado produce un ítem que no sabe si paga ISV y el error aparece al facturar.
        _ = await fiscal.GetTaxCodeAsync(cmd.OrganizationId, cmd.TaxCodeId, ct)
            ?? throw new ArgumentException(
                "El tributo no existe en esta organización.", nameof(cmd));

        var item = BillableItem.Create(
            cmd.OrganizationId, cmd.Code, cmd.Name,
            Parse<BillableItemKind>(cmd.Kind, BillableItemKind.Service),
            cmd.TaxCodeId, cmd.DefaultPrice,
            Parse(cmd.PaymentTiming, Domain.Billing.Catalog.PaymentTiming.Either),
            Parse(cmd.Component, BillableComponent.Global),
            cmd.LabTestId, cmd.LabPanelId, stockItemId: null, cmd.ServiceCode);

        await repo.AddItemAsync(item, ct);
        return BillableItemMapper.ToDto(item);
    }

    private static T Parse<T>(string? value, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var parsed) ? parsed : fallback;
}

/// <summary>
/// Resuelve el precio de un cobrable y devuelve <b>por qué</b> es ese.
///
/// <para>La explicación no es adorno: "¿por qué a este paciente se le cobró esto?" es la pregunta
/// más frecuente en una auditoría de facturación, y en los sistemas locales se responde llamando
/// a administración.</para>
/// </summary>
public sealed class ResolvePriceHandler(
    IBillingCatalogRepository repo, IClinicClock clock)
{
    public async Task<PricedItemDto?> HandleAsync(
        Guid orgId, Guid itemId, Guid? payerContractId = null, CancellationToken ct = default)
    {
        var item = await repo.GetItemAsync(orgId, itemId, ct);
        if (item is null)
        {
            return null;
        }

        var candidatos = await repo.GetPriceCandidatesAsync(orgId, itemId, ct);
        var resuelto = PriceResolver.Resolve(item, candidatos, clock.Today(), payerContractId);

        return new PricedItemDto(
            BillableItemMapper.ToDto(item),
            resuelto.Price,
            resuelto.Source.ToString(),
            resuelto.PriceListId,
            resuelto.PriceListName,
            resuelto.MinPrice,
            resuelto.DiscountPctMax,
            resuelto.Explain());
    }
}

// ── Tarifarios ────────────────────────────────────────────────────────────

public sealed record PriceListDto(
    Guid Id,
    string Code,
    string Name,
    string Kind,
    string Currency,
    int Priority,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool Active,
    bool InEffect);

public sealed record PriceListItemDto(
    Guid Id,
    Guid BillableItemId,
    string BillableItemCode,
    string BillableItemName,
    decimal Price,
    decimal? MinPrice,
    decimal? DiscountPctMax,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    bool InEffect);

public sealed class ListPriceListsHandler(IBillingCatalogRepository repo, IClinicClock clock)
{
    public async Task<IReadOnlyList<PriceListDto>> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var today = clock.Today();
        var rows = await repo.ListPriceListsAsync(orgId, ct);
        return rows.Select(l => new PriceListDto(
            l.Id, l.Code, l.Name, l.Kind.ToString(), l.Currency, l.Priority,
            l.ValidFrom, l.ValidTo, l.Active, l.IsInEffectOn(today))).ToList();
    }
}

public sealed record CreatePriceListCommand(
    Guid OrganizationId,
    string Code,
    string Name,
    string Kind,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int Priority,
    string? Currency);

public sealed class CreatePriceListHandler(IBillingCatalogRepository repo, IClinicClock clock)
{
    public async Task<PriceListDto> HandleAsync(CreatePriceListCommand cmd, CancellationToken ct = default)
    {
        if (await repo.PriceListCodeExistsAsync(cmd.OrganizationId, cmd.Code, ct))
        {
            throw new InvalidOperationException($"Ya existe un tarifario con el código '{cmd.Code}'.");
        }

        var lista = PriceList.Create(
            cmd.OrganizationId, cmd.Code, cmd.Name,
            Enum.TryParse<PriceListKind>(cmd.Kind, ignoreCase: true, out var k) ? k : PriceListKind.Public,
            cmd.ValidFrom, cmd.ValidTo, cmd.Priority, cmd.Currency ?? "HNL");

        await repo.AddPriceListAsync(lista, ct);
        var today = clock.Today();
        return new PriceListDto(
            lista.Id, lista.Code, lista.Name, lista.Kind.ToString(), lista.Currency, lista.Priority,
            lista.ValidFrom, lista.ValidTo, lista.Active, lista.IsInEffectOn(today));
    }
}

public sealed class ListPriceListItemsHandler(IBillingCatalogRepository repo, IClinicClock clock)
{
    public async Task<IReadOnlyList<PriceListItemDto>> HandleAsync(
        Guid orgId, Guid priceListId, CancellationToken ct = default)
    {
        var today = clock.Today();
        var precios = await repo.ListPriceListItemsAsync(orgId, priceListId, ct);

        // Los nombres se resuelven en un solo barrido: una consulta por línea convertiría abrir
        // un tarifario de 200 ítems en 200 viajes a la base.
        var items = (await repo.SearchItemsAsync(orgId, null, activeOnly: false, limit: 1000, ct))
            .ToDictionary(i => i.Id);

        return precios.Select(p =>
        {
            items.TryGetValue(p.BillableItemId, out var item);
            return new PriceListItemDto(
                p.Id, p.BillableItemId,
                item?.Code ?? "(?)", item?.Name ?? "(cobrable eliminado)",
                p.Price, p.MinPrice, p.DiscountPctMax,
                p.ValidFrom, p.ValidTo, p.IsInEffectOn(today));
        }).ToList();
    }
}

public sealed record SetPriceCommand(
    Guid OrganizationId,
    Guid PriceListId,
    Guid BillableItemId,
    decimal Price,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal? MinPrice,
    decimal? DiscountPctMax);

public sealed class SetPriceHandler(IBillingCatalogRepository repo)
{
    public async Task<PriceListItemDto> HandleAsync(SetPriceCommand cmd, CancellationToken ct = default)
    {
        _ = await repo.GetPriceListAsync(cmd.OrganizationId, cmd.PriceListId, ct)
            ?? throw new ArgumentException("El tarifario no existe.", nameof(cmd));

        var item = await repo.GetItemAsync(cmd.OrganizationId, cmd.BillableItemId, ct)
            ?? throw new ArgumentException("El cobrable no existe.", nameof(cmd));

        var precio = PriceListItem.Create(
            cmd.OrganizationId, cmd.PriceListId, cmd.BillableItemId, cmd.Price,
            cmd.ValidFrom, cmd.ValidTo, cmd.MinPrice, cmd.DiscountPctMax);

        await repo.AddPriceListItemAsync(precio, ct);

        return new PriceListItemDto(
            precio.Id, item.Id, item.Code, item.Name, precio.Price,
            precio.MinPrice, precio.DiscountPctMax, precio.ValidFrom, precio.ValidTo,
            InEffect: precio.IsInEffectOn(cmd.ValidFrom));
    }
}
