using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// POS y cuentas bancarias de las sedes, para separar el cierre por banco. Bajo
/// <c>/api/billing/payment-terminals</c>. Darlos de alta es del jefe administrativo; el cajero
/// solo lista los de su turno.
/// </summary>
public static class BillingPaymentTerminalEndpoints
{
    public static IEndpointRouteBuilder MapBillingPaymentTerminalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/payment-terminals").WithTags("Billing · Caja");

        group.MapGet("/", ListAsync)
            .WithName("ListPaymentTerminals")
            .WithSummary("POS y cuentas bancarias de la organización o de una sede, inactivos incluidos.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapGet("/mine", MineAsync)
            .WithName("ListMySessionPaymentTerminals")
            .WithSummary("Los activos de la sede de la caja donde el usuario tiene el turno abierto.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        group.MapPost("/", CreateAsync)
            .WithName("CreatePaymentTerminal")
            .WithSummary("Da de alta un POS (tarjeta) o una cuenta bancaria (transferencia) en una sede.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapPatch("/{terminalId:guid}", SetActiveAsync)
            .WithName("SetPaymentTerminalActive")
            .WithSummary("Activa o desactiva. No se borra: los cierres pasados lo siguen nombrando.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> ListAsync(
        Guid? locationId, ClaimsPrincipal principal, PaymentTerminalHandlers handler, CancellationToken ct) =>
        Org(principal) is { } org ? Results.Ok(await handler.ListAsync(org, locationId, ct)) : Results.Forbid();

    private static async Task<IResult> MineAsync(
        ClaimsPrincipal principal, PaymentTerminalHandlers handler, CancellationToken ct)
    {
        if (Org(principal) is not { } org || User(principal) is not { } user)
        {
            return Results.Forbid();
        }
        return Results.Ok(await handler.ForMySessionAsync(org, user, ct));
    }

    private static async Task<IResult> CreateAsync(
        CreatePaymentTerminalRequest request, ClaimsPrincipal principal, PaymentTerminalHandlers handler, CancellationToken ct)
    {
        if (Org(principal) is not { } org)
        {
            return Results.Forbid();
        }
        try
        {
            var t = await handler.CreateAsync(
                new CreatePaymentTerminalCommand(org, request.LocationId, request.Name, request.Bank, request.Kind), ct);
            return Results.Created($"/api/billing/payment-terminals/{t.Id}", t);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> SetActiveAsync(
        Guid terminalId, SetPaymentTerminalActiveRequest request, ClaimsPrincipal principal,
        PaymentTerminalHandlers handler, CancellationToken ct)
    {
        if (Org(principal) is not { } org)
        {
            return Results.Forbid();
        }
        var t = await handler.SetActiveAsync(org, terminalId, request.Active, ct);
        return t is null ? Results.NotFound() : Results.Ok(t);
    }

    private static Guid? Org(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue("organization_id"), out var o) ? o : null;

    private static Guid? User(ClaimsPrincipal p) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier) ?? p.FindFirstValue("sub"), out var u) ? u : null;
}

public sealed record CreatePaymentTerminalRequest(Guid LocationId, string Name, string Bank, string Kind);

public sealed record SetPaymentTerminalActiveRequest(bool Active);
