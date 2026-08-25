using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Puerta de prepago (FAC-7, hueco G2). Bajo <c>/api/billing/gate</c>.
///
/// <para><b>Consultar la puerta lo puede hacer cualquiera con sesión</b>, no solo el módulo de
/// facturación: quien pregunta es el laboratorio, la recepción clínica, la farmacia. Exigirles un
/// permiso de facturación para saber si algo está pagado los obligaría a tenerlo, y con él
/// vendría todo lo demás.</para>
///
/// <para><b>Liberar sí es del módulo:</b> dar un servicio sin cobrarlo es una decisión con
/// consecuencia económica.</para>
/// </summary>
public static class BillingGateEndpoints
{
    public static IEndpointRouteBuilder MapBillingGateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/gate")
            .WithTags("Billing · Puerta de prepago")
            .RequireAuthorization();

        group.MapGet("/", EvaluateAsync)
            .WithName("EvaluateServiceGate")
            .WithSummary("¿Este servicio puede ejecutarse? Devuelve veredicto, motivo y texto para mostrar.");

        group.MapPost("/waivers", WaiveAsync)
            .WithName("WaiveService")
            .WithSummary("Libera un servicio sin cobrarlo, dejando constancia. Idempotente.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/waivers", ListWaiversAsync)
            .WithName("ListServiceWaivers")
            .WithSummary("Servicios dados sin cobrar en un rango de fechas.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        return app;
    }

    private static async Task<IResult> EvaluateAsync(
        string sourceRef, string sourceId, string? sourceKind, string? priority,
        ClaimsPrincipal principal, EvaluateServiceGateHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        var kind = Enum.TryParse<ChargeSourceKind>(sourceKind, ignoreCase: true, out var k)
            ? k : ChargeSourceKind.LabOrder;

        return Results.Ok(await handler.HandleAsync(
            new GateQuery(orgId.Value, sourceRef, sourceId, kind, priority), ct));
    }

    private static async Task<IResult> WaiveAsync(
        WaiveServiceRequest request, ClaimsPrincipal principal,
        WaiveServiceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new WaiveServiceCommand(
                orgId.Value, request.SourceRef, userId.Value, request.Reason, request.AccountId), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message,
                statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ListWaiversAsync(
        DateOnly? since, DateOnly? until, ClaimsPrincipal principal,
        ListServiceWaiversHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, since, until, ct));
    }

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin identidad",
            detail: "El token no trae organization_id o sub.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

public sealed record WaiveServiceRequest(string SourceRef, string Reason, Guid? AccountId);
