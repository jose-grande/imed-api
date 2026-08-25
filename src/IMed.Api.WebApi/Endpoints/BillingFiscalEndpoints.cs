using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Motor fiscal: autorizaciones (B23) y puntos de cobro (B24). Bajo <c>/api/billing/fiscal</c>.
///
/// <para>Leer lo puede hacer cualquiera del módulo —la caja necesita saber con qué autorización
/// va a emitir—, pero dar de alta un CAI o una caja es del jefe administrativo: son las decisiones
/// que comprometen a la organización ante la autoridad fiscal.</para>
/// </summary>
public static class BillingFiscalEndpoints
{
    public static IEndpointRouteBuilder MapBillingFiscalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/fiscal")
            .WithTags("Billing · Fiscal")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapGet("/setup", GetSetupAsync)
            .WithName("GetFiscalSetup")
            .WithSummary("Régimen, tipos de documento y tributos configurados.");

        group.MapPost("/setup", ProvisionSetupAsync)
            .WithName("ProvisionFiscalRegime")
            .WithSummary("Deja la organización lista para emitir bajo un régimen. Idempotente.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/authorizations", ListAuthorizationsAsync)
            .WithName("ListFiscalAuthorizations")
            .WithSummary("Autorizaciones fiscales con su consumo y sus avisos.");

        group.MapPost("/authorizations", RegisterAuthorizationAsync)
            .WithName("RegisterFiscalAuthorization")
            .WithSummary("Registra una autorización (CAI) con su rango y fecha límite.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapPost("/authorizations/{authorizationId:guid}/revoke", RevokeAuthorizationAsync)
            .WithName("RevokeFiscalAuthorization")
            .WithSummary("Da de baja una autorización. No devuelve los correlativos ya emitidos.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/cash-points", ListCashPointsAsync)
            .WithName("ListCashPoints")
            .WithSummary("Cajas de la organización y con quién comparten punto de emisión.");

        group.MapPost("/cash-points", CreateCashPointAsync)
            .WithName("CreateCashPoint")
            .WithSummary("Da de alta una caja. Queda operativa sin reiniciar nada.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapPatch("/cash-points/{cashPointId:guid}", SetCashPointActiveAsync)
            .WithName("SetCashPointActive")
            .WithSummary("Abre o cierra una caja.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> GetSetupAsync(
        ClaimsPrincipal principal, GetFiscalSetupHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> ProvisionSetupAsync(
        ProvisionFiscalRequest request, ClaimsPrincipal principal,
        ProvisionFiscalRegimeHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(
                orgId.Value, request.RegimeCode ?? "HN_SAR_CAI", ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ListAuthorizationsAsync(
        ClaimsPrincipal principal, ListFiscalAuthorizationsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> RegisterAuthorizationAsync(
        RegisterAuthorizationRequest request, ClaimsPrincipal principal,
        RegisterFiscalAuthorizationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var creada = await handler.HandleAsync(new RegisterFiscalAuthorizationCommand(
                orgId.Value, request.LocationId, request.DocumentTypeId, request.Cai,
                request.EmissionPointCode, request.RangeFrom, request.RangeTo, request.LimitDate), ct);
            return Results.Ok(creada);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> RevokeAuthorizationAsync(
        Guid authorizationId, ClaimsPrincipal principal,
        RevokeFiscalAuthorizationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, authorizationId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListCashPointsAsync(
        ClaimsPrincipal principal, ListCashPointsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> CreateCashPointAsync(
        CreateCashPointRequest request, ClaimsPrincipal principal,
        CreateCashPointHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var creada = await handler.HandleAsync(new CreateCashPointCommand(
                orgId.Value, request.LocationId, request.Code, request.Name,
                request.EmissionPointCode, request.Profile,
                request.IsTemporary ?? false, request.PrinterProfile), ct);
            return Results.Ok(creada);
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

    private static async Task<IResult> SetCashPointActiveAsync(
        Guid cashPointId, SetCashPointActiveRequest request, ClaimsPrincipal principal,
        SetCashPointActiveHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, cashPointId, request.Active, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El token no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

public sealed record ProvisionFiscalRequest(string? RegimeCode);

public sealed record RegisterAuthorizationRequest(
    Guid LocationId,
    Guid DocumentTypeId,
    string Cai,
    string EmissionPointCode,
    long RangeFrom,
    long RangeTo,
    DateOnly LimitDate);

public sealed record CreateCashPointRequest(
    Guid LocationId,
    string Code,
    string Name,
    string EmissionPointCode,
    string? Profile,
    bool? IsTemporary,
    string? PrinterProfile);

public sealed record SetCashPointActiveRequest(bool Active);
