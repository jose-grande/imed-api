using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Caja: turno, cobro y arqueo (FAC-6, pantallas B2 y B3). Bajo <c>/api/billing/cash</c>.
///
/// <para>🔴 <b>No hay endpoint que devuelva el efectivo esperado de un turno abierto.</b> Es
/// deliberado: si el número se puede consultar, tarde o temprano alguna pantalla lo muestra, y el
/// arqueo a ciegas deja de serlo. El esperado se calcula al cerrar y viaja en esa respuesta.</para>
/// </summary>
public static class BillingCashEndpoints
{
    public static IEndpointRouteBuilder MapBillingCashEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/cash")
            .WithTags("Billing · Caja")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        group.MapGet("/session", GetOpenAsync)
            .WithName("GetOpenCashSession")
            .WithSummary("Turno abierto del cajero. No incluye el efectivo esperado.");

        group.MapPost("/session", OpenAsync)
            .WithName("OpenCashSession")
            .WithSummary("Abre un turno declarando el monto inicial.");

        group.MapPost("/session/{sessionId:guid}/close", CloseAsync)
            .WithName("CloseCashSession")
            .WithSummary("Cierra el turno con lo contado. El esperado se calcula acá y recién acá se revela.");

        group.MapPost("/session/{sessionId:guid}/handover", HandoverAsync)
            .WithName("HandoverCashSession")
            .WithSummary("Traspaso de cajero: cierra el turno del saliente y abre el del entrante.");

        group.MapPost("/session/{sessionId:guid}/movements", MovementAsync)
            .WithName("RegisterCashMovement")
            .WithSummary("Retiro, depósito o ajuste de efectivo, siempre con motivo.");

        group.MapGet("/session/{sessionId:guid}/payments", PaymentsAsync)
            .WithName("ListSessionPayments")
            .WithSummary("Cobros del turno.");

        group.MapPost("/payments", ReceiveAsync)
            .WithName("ReceivePayment")
            .WithSummary("Cobra una o varias facturas con uno o varios métodos.");

        return app;
    }

    private static async Task<IResult> GetOpenAsync(
        ClaimsPrincipal principal, GetOpenSessionHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        var turno = await handler.HandleAsync(orgId.Value, userId.Value, ct);
        return turno is null ? Results.NoContent() : Results.Ok(turno);
    }

    private static async Task<IResult> OpenAsync(
        OpenSessionRequest request, ClaimsPrincipal principal,
        OpenCashSessionHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        var r = await handler.HandleAsync(
            orgId.Value, request.CashPointId, userId.Value, request.OpeningAmount, ct);
        return r.Succeeded ? Results.Ok(r.Session) : Conflict(r.Code, r.Message);
    }

    private static async Task<IResult> CloseAsync(
        Guid sessionId, CloseSessionRequest request, ClaimsPrincipal principal,
        CloseCashSessionHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        var r = await handler.HandleAsync(new CloseCashSessionCommand(
            orgId.Value, sessionId, userId.Value,
            request.CountedAmount, request.Note, request.AuthorizedBy), ct);
        return r.Succeeded ? Results.Ok(r.Session) : Conflict(r.Code, r.Message);
    }

    private static async Task<IResult> HandoverAsync(
        Guid sessionId, HandoverRequest request, ClaimsPrincipal principal,
        HandoverCashSessionHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        var r = await handler.HandleAsync(new HandoverCommand(
            orgId.Value, sessionId, userId.Value, request.IncomingUserId,
            request.CountedAmount, request.Note, request.AuthorizedBy), ct);

        return r.Succeeded
            ? Results.Ok(new { closed = r.Closed, opened = r.Opened })
            : Conflict(r.Code, r.Message);
    }

    private static async Task<IResult> MovementAsync(
        Guid sessionId, MovementRequest request, ClaimsPrincipal principal,
        RegisterCashMovementHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            var m = await handler.HandleAsync(new RegisterMovementCommand(
                orgId.Value, sessionId, request.Kind, request.Amount,
                request.Reason, userId.Value, request.AuthorizedBy), ct);
            return m is null ? Results.NotFound() : Results.Ok(m);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> PaymentsAsync(
        Guid sessionId, ClaimsPrincipal principal,
        ListSessionPaymentsHandler handler, CancellationToken ct)
    {
        var (orgId, _) = Identity(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, sessionId, ct));
    }

    private static async Task<IResult> ReceiveAsync(
        ReceivePaymentRequest request, ClaimsPrincipal principal,
        ReceivePaymentHandler handler, CancellationToken ct)
    {
        var (orgId, userId) = Identity(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }

        var r = await handler.HandleAsync(new ReceivePaymentCommand(
            orgId.Value, request.SessionId, request.PartyId, userId.Value,
            request.InvoiceIds ?? [],
            (request.Tenders ?? []).Select(t => new TenderLine(t.Method, t.Amount, t.Reference)).ToList()),
            ct);

        return r.Succeeded ? Results.Ok(r) : Conflict(r.Code, r.Message);
    }

    private static IResult Conflict(string? code, string? message) =>
        Results.Problem(
            title: "No se pudo completar la operación de caja",
            detail: message,
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?> { ["code"] = code });

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin identidad",
            detail: "El token no trae organization_id o sub.",
            statusCode: StatusCodes.Status403Forbidden);

    private static (Guid? OrgId, Guid? UserId) Identity(ClaimsPrincipal principal)
    {
        var org = Guid.TryParse(principal.FindFirstValue("organization_id"), out var o) ? o : (Guid?)null;
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var user = Guid.TryParse(raw, out var u) ? u : (Guid?)null;
        return (org, user);
    }
}

public sealed record OpenSessionRequest(Guid CashPointId, decimal OpeningAmount);

public sealed record CloseSessionRequest(decimal CountedAmount, string? Note, Guid? AuthorizedBy);

public sealed record HandoverRequest(
    Guid IncomingUserId, decimal CountedAmount, string? Note, Guid? AuthorizedBy);

public sealed record MovementRequest(
    string Kind, decimal Amount, string Reason, Guid? AuthorizedBy);

public sealed record TenderRequest(string Method, decimal Amount, string? Reference);

public sealed record ReceivePaymentRequest(
    Guid SessionId,
    Guid PartyId,
    IReadOnlyList<Guid>? InvoiceIds,
    IReadOnlyList<TenderRequest>? Tenders);
