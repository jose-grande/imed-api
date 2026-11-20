using IMed.Api.Application.Abstractions;
using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Facturar y cobrar una orden externa desde la recepción del laboratorio. Bajo
/// <c>/api/lab/reception/checkout</c>. Ver la orden pide <c>billing.read</c>; cobrar, las mismas
/// reglas que el cobro de cuentas: con pagos <c>billing.collect</c>, sin pagos <c>billing.invoice</c>.
/// </summary>
public static class LabCheckoutEndpoints
{
    public static IEndpointRouteBuilder MapLabCheckoutEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/lab/reception/checkout")
            .WithTags("LabReception")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

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
        string requisition, LabCheckoutInput body, ClaimsPrincipal p, LabOrderCheckoutHandler h,
        IPermissionResolver permisos, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (EndpointRun.User(p) is not { } user)
            {
                return EndpointRun.Forbidden("El token no trae usuario.");
            }
            var permiso = body.Tenders is { Count: > 0 } ? PermissionCatalog.BillingCollect : PermissionCatalog.BillingInvoice;
            if (!await EndpointRun.HasAsync(p, permisos, org, permiso, ct))
            {
                return EndpointRun.Forbidden("Sin permiso para esta operación de cobro.");
            }
            return await h.CheckoutAsync(org, user, requisition, body, ct) is { } r ? Results.Ok(r) : Results.NotFound();
        });
}
