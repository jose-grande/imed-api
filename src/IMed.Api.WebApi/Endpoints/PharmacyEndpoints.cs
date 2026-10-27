using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Pharmacy;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Farmacia (plan de salida a producción, B4 y B5). Bajo <c>/api/pharmacy</c>. Despachar recetas
/// pide <c>pharmacy.dispense</c>; vender en mostrador, <c>pharmacy.sell</c>.
/// </summary>
public static class PharmacyEndpoints
{
    public static IEndpointRouteBuilder MapPharmacyEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/pharmacy").WithTags("Farmacia").RequireAuthorization();
        var despachar = PermissionPolicy.For(PermissionCatalog.PharmacyDispense);
        var vender = PermissionPolicy.For(PermissionCatalog.PharmacySell);

        g.MapGet("/queue", QueueAsync).RequireAuthorization(despachar)
            .WithSummary("Recetas de las consultas de la sede por despachar, con artículo propuesto.");
        g.MapGet("/candidates", CandidatesAsync).RequireAuthorization(despachar)
            .WithSummary("Artículos vendibles por nombre, para recetas en texto libre o sustituciones.");
        g.MapPost("/dispenses", DispenseAsync).RequireAuthorization(despachar)
            .WithSummary("Despacha una receta: salida FEFO + cargo en la cuenta de la consulta, en una transacción.");
        g.MapGet("/dispenses", ListAsync).WithSummary("Entregas de la sede (despachos y ventas).");
        g.MapGet("/dispenses/{id:guid}", GetAsync);

        g.MapGet("/sale-items", SaleItemsAsync).RequireAuthorization(vender)
            .WithSummary("Artículos de venta libre con existencia y precio.");
        g.MapPost("/sales", SellAsync).RequireAuthorization(vender)
            .WithSummary("Venta libre: salida + cargo, factura y (opcional) cobro en caja de farmacia o mixta.");
        g.MapPost("/sales/{id:guid}/invoice", InvoiceAsync).RequireAuthorization(vender);
        g.MapPost("/sales/{id:guid}/collect", CollectAsync).RequireAuthorization(vender);
        g.MapPost("/sales/{id:guid}/cancel", CancelAsync).RequireAuthorization(vender);
        g.MapGet("/sales/{id:guid}/invoice/pdf", InvoicePdfAsync).RequireAuthorization(vender)
            .WithSummary("PDF de la factura de una venta libre (ticket con format=thermal80), para el mostrador.");
        return app;
    }

    private static Task<IResult> QueueAsync(
        Guid? warehouseId, int? days, ClaimsPrincipal p, PrescriptionQueueHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.HandleAsync(org, EndpointRun.User(p), warehouseId, days ?? 7, ct)));

    private static Task<IResult> CandidatesAsync(
        string? q, Guid? warehouseId, ClaimsPrincipal p, PrescriptionQueueHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(string.IsNullOrWhiteSpace(q)
            ? []
            : await h.SearchCandidatesAsync(org, EndpointRun.User(p), warehouseId, q, overTheCounterOnly: false, ct)));

    private static Task<IResult> SaleItemsAsync(
        string? q, Guid? warehouseId, ClaimsPrincipal p, PrescriptionQueueHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(string.IsNullOrWhiteSpace(q)
            ? []
            : await h.SearchCandidatesAsync(org, EndpointRun.User(p), warehouseId, q, overTheCounterOnly: true, ct)));

    private static Task<IResult> DispenseAsync(
        DispensePrescriptionInput body, ClaimsPrincipal p, DispensePrescriptionHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var r = await h.HandleAsync(org, EndpointRun.User(p), body, ct);
            // 200 y no 201 si ya estaba hecho: la misma llave no despacha dos veces.
            return r.AlreadyProcessed ? Results.Ok(r) : Results.Created($"/api/pharmacy/dispenses/{r.Dispense.Id}", r);
        });

    private static Task<IResult> ListAsync(
        string? kind, DateOnly? from, DateOnly? to, ClaimsPrincipal p, IPermissionResolver permisos,
        ListDispensesHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (!await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.PharmacyDispense, ct)
                && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.PharmacySell, ct))
            {
                return EndpointRun.Forbidden("Ver las entregas de farmacia requiere despachar o vender.");
            }
            return Results.Ok(await h.HandleAsync(org, EndpointRun.User(p), kind, from, to, ct));
        });

    private static Task<IResult> GetAsync(
        Guid id, ClaimsPrincipal p, IPermissionResolver permisos, DispenseReader h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (!await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.PharmacyDispense, ct)
                && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.PharmacySell, ct))
            {
                return EndpointRun.Forbidden("Ver las entregas de farmacia requiere despachar o vender.");
            }
            return await h.GetAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound();
        });

    private static Task<IResult> SellAsync(
        OverTheCounterSaleInput body, ClaimsPrincipal p, OverTheCounterSaleHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (EndpointRun.User(p) is not { } user)
            {
                return EndpointRun.Forbidden("La venta necesita saber quién la registra.");
            }
            var r = await h.SellAsync(org, user, body, ct);
            return r.AlreadyProcessed ? Results.Ok(r) : Results.Created($"/api/pharmacy/dispenses/{r.Sale.Id}", r);
        });

    private static Task<IResult> InvoiceAsync(Guid id, ClaimsPrincipal p, OverTheCounterSaleHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => EndpointRun.User(p) is not { } user
            ? EndpointRun.Forbidden("La factura necesita saber quién la emite.")
            : await h.InvoiceAsync(org, user, id, ct) is { } r ? Results.Ok(r) : Results.NotFound());

    private static Task<IResult> CollectAsync(
        Guid id, CollectSaleInput body, ClaimsPrincipal p, OverTheCounterSaleHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => EndpointRun.User(p) is not { } user
            ? EndpointRun.Forbidden("El cobro necesita saber quién lo recibe.")
            : await h.CollectAsync(org, user, id, body, ct) is { } r ? Results.Ok(r) : Results.NotFound());

    /// <summary>
    /// El mostrador imprime la factura de su venta sin necesitar el permiso de lectura de todo
    /// facturación: solo la de una venta que existe en farmacia.
    /// </summary>
    private static Task<IResult> InvoicePdfAsync(
        Guid id, string? format, bool? reprint, ClaimsPrincipal p, DispenseReader ventas, GetInvoicePdfHandler pdfs,
        CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var venta = await ventas.GetAsync(org, id, ct);
            if (venta?.InvoiceId is not { } facturaId)
            {
                return Results.NotFound();
            }
            var plantilla = string.Equals(format, "thermal80", StringComparison.OrdinalIgnoreCase)
                ? InvoicePrintFormat.Thermal80
                : InvoicePrintFormat.Letter;
            var pdf = await pdfs.HandleAsync(org, facturaId, plantilla, reprint ?? false, ct);
            return pdf is null ? Results.NotFound() : Results.File(pdf.Bytes, "application/pdf", pdf.FileName);
        });

    private static Task<IResult> CancelAsync(
        Guid id, ReasonRequest body, ClaimsPrincipal p, OverTheCounterSaleHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => EndpointRun.User(p) is not { } user
            ? EndpointRun.Forbidden("La anulación necesita saber quién la hace.")
            : await h.CancelAsync(org, user, id, body.Reason, ct) is { } r ? Results.Ok(r) : Results.NotFound());
}
