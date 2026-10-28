using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Facturar y cobrar una orden externa desde la recepción del laboratorio. Bajo
/// <c>/api/lab/reception/checkout</c>; pide <c>billing.collect</c>, el mismo permiso que emitir una
/// factura y cobrar en caja: quien no es cajero no ve el panel.
/// </summary>
public static class LabCheckoutEndpoints
{
    public static IEndpointRouteBuilder MapLabCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/lab/reception/checkout")
            .WithTags("LabReception")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        g.MapGet("/{requisition}", GetAsync)
            .WithName("GetLabCheckout")
            .WithSummary("Cargos, facturas y si se puede facturar y cobrar una orden externa en la recepción.");
        g.MapPost("/{requisition}", CheckoutAsync)
            .WithName("LabCheckout")
            .WithSummary("Factura la orden externa y, si trae pagos, la cobra en el turno del usuario.");
        return app;
    }

    private static Task<IResult> GetAsync(
        string requisition, ClaimsPrincipal p, LabOrderCheckoutHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => EndpointRun.User(p) is not { } user
            ? EndpointRun.Forbidden("El token no trae usuario.")
            : await h.GetAsync(org, user, requisition, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> CheckoutAsync(
        string requisition, LabCheckoutInput body, ClaimsPrincipal p, LabOrderCheckoutHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => EndpointRun.User(p) is not { } user
            ? EndpointRun.Forbidden("El token no trae usuario.")
            : await h.CheckoutAsync(org, user, requisition, body, ct) is { } r ? Results.Ok(r) : Results.NotFound());
}
