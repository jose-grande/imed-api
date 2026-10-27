using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Catálogo de cobrables y tarifarios (FAC-3, pantallas B20 y B21). Bajo <c>/api/billing/catalog</c>.
///
/// <para>Buscar y ver precios lo hace cualquiera del módulo —de ahí come la caja—, pero definir
/// qué se cobra y a cuánto es del jefe administrativo: es la decisión de márgenes de la clínica.</para>
/// </summary>
public static class BillingCatalogEndpoints
{
    public static IEndpointRouteBuilder MapBillingCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/catalog")
            .WithTags("Billing · Catálogo")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapGet("/items", SearchItemsAsync)
            .WithName("SearchBillableItems")
            .WithSummary("Busca cobrables por código o nombre.");

        group.MapPost("/items", CreateItemAsync)
            .WithName("CreateBillableItem")
            .WithSummary("Da de alta un cobrable.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/items/price-pending", ListPricePendingAsync)
            .WithName("ListPricePendingBillableItems")
            .WithSummary("Cobrables sin precio: no se facturan hasta que alguien fije su precio.");

        group.MapPatch("/items/{itemId:guid}", UpdateItemAsync)
            .WithName("UpdateBillableItem")
            .WithSummary("Edita un cobrable. Fijar el precio resuelve el 'sin precio' y lo aplica a los cargos que esperaban.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/items/{itemId:guid}/price", ResolvePriceAsync)
            .WithName("ResolveBillablePrice")
            .WithSummary("Precio resuelto de un cobrable, con la razón por la que es ese.");

        group.MapGet("/price-lists", ListPriceListsAsync)
            .WithName("ListPriceLists")
            .WithSummary("Tarifarios con su vigencia.");

        group.MapPost("/price-lists", CreatePriceListAsync)
            .WithName("CreatePriceList")
            .WithSummary("Crea un tarifario.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/price-lists/{priceListId:guid}/items", ListPriceListItemsAsync)
            .WithName("ListPriceListItems")
            .WithSummary("Precios de un tarifario.");

        group.MapPost("/price-lists/{priceListId:guid}/items", SetPriceAsync)
            .WithName("SetPrice")
            .WithSummary("Fija el precio de un cobrable en un tarifario, con su vigencia.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> SearchItemsAsync(
        string? term, bool? activeOnly, int? limit, ClaimsPrincipal principal,
        SearchBillableItemsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, term, activeOnly ?? true, limit ?? 50, ct));
    }

    private static async Task<IResult> CreateItemAsync(
        CreateBillableItemRequest request, ClaimsPrincipal principal,
        CreateBillableItemHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new CreateBillableItemCommand(
                orgId.Value, request.Code, request.Name, request.Kind, request.TaxCodeId,
                request.DefaultPrice, request.PaymentTiming, request.Component,
                request.LabTestId, request.LabPanelId, request.ServiceCode), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListPricePendingAsync(
        ClaimsPrincipal principal, ListPricePendingItemsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> UpdateItemAsync(
        Guid itemId, UpdateBillableItemRequest request, ClaimsPrincipal principal,
        UpdateBillableItemHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var r = await handler.HandleAsync(new UpdateBillableItemCommand(
                orgId.Value, itemId, request.Name, request.TaxCodeId, request.DefaultPrice,
                request.PaymentTiming, request.Active), ct);
            return r is null ? Results.NotFound() : Results.Ok(r);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ResolvePriceAsync(
        Guid itemId, Guid? payerContractId, ClaimsPrincipal principal,
        ResolvePriceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var precio = await handler.HandleAsync(orgId.Value, itemId, payerContractId, ct);
        return precio is null ? Results.NotFound() : Results.Ok(precio);
    }

    private static async Task<IResult> ListPriceListsAsync(
        ClaimsPrincipal principal, ListPriceListsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> CreatePriceListAsync(
        CreatePriceListRequest request, ClaimsPrincipal principal,
        CreatePriceListHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new CreatePriceListCommand(
                orgId.Value, request.Code, request.Name, request.Kind,
                request.ValidFrom, request.ValidTo, request.Priority ?? 0, request.Currency), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ListPriceListItemsAsync(
        Guid priceListId, ClaimsPrincipal principal,
        ListPriceListItemsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, priceListId, ct));
    }

    private static async Task<IResult> SetPriceAsync(
        Guid priceListId, SetPriceRequest request, ClaimsPrincipal principal,
        SetPriceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new SetPriceCommand(
                orgId.Value, priceListId, request.BillableItemId, request.Price,
                request.ValidFrom, request.ValidTo, request.MinPrice, request.DiscountPctMax), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El token no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

public sealed record CreateBillableItemRequest(
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

public sealed record UpdateBillableItemRequest(
    string Name,
    Guid TaxCodeId,
    decimal DefaultPrice,
    string? PaymentTiming,
    bool? Active);

public sealed record CreatePriceListRequest(
    string Code,
    string Name,
    string Kind,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    int? Priority,
    string? Currency);

public sealed record SetPriceRequest(
    Guid BillableItemId,
    decimal Price,
    DateOnly ValidFrom,
    DateOnly? ValidTo,
    decimal? MinPrice,
    decimal? DiscountPctMax);
