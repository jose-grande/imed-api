using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Reportes de cierre fiscal (FAC-8). Bajo <c>/api/billing/reports</c>.
///
/// <para>No son analítica —eso es U4— sino <b>obligación</b>: el contador no puede declarar sin el
/// reporte de ISV, y una auditoría pregunta por los huecos de correlativo. Por eso salen con la
/// ola y no después.</para>
///
/// <para>Leer los reportes exige <c>billing.read</c>; el de excepciones exige supervisar, porque
/// nombra a quién autorizó qué.</para>
/// </summary>
public static class BillingReportEndpoints
{
    public static IEndpointRouteBuilder MapBillingReportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/reports")
            .WithTags("Billing · Reportes")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapGet("/cash-cut", CashCutAsync)
            .WithName("GetDailyCashCut")
            .WithSummary("Corte de caja del día, por caja y por cajero.");

        group.MapGet("/tax", TaxAsync)
            .WithName("GetTaxReport")
            .WithSummary("ISV por tratamiento tributario, para declarar.");

        group.MapGet("/ledger/{authorizationId:guid}", LedgerAsync)
            .WithName("GetDocumentLedger")
            .WithSummary("Libro de documentos de una autorización, con los huecos identificados.");

        group.MapGet("/exceptions", ExceptionsAsync)
            .WithName("GetExceptionReport")
            .WithSummary("Cargos manuales, descuentos autorizados, servicios liberados y descuadres.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> CashCutAsync(
        DateOnly? day, Guid? cashPointId, ClaimsPrincipal principal,
        GetDailyCashCutHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, day, cashPointId, ct));
    }

    private static async Task<IResult> TaxAsync(
        DateOnly? since, DateOnly? until, ClaimsPrincipal principal,
        GetTaxReportHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, since, until, ct));
    }

    private static async Task<IResult> LedgerAsync(
        Guid authorizationId, ClaimsPrincipal principal,
        GetDocumentLedgerHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var libro = await handler.HandleAsync(orgId.Value, authorizationId, ct);
        return libro is null ? Results.NotFound() : Results.Ok(libro);
    }

    private static async Task<IResult> ExceptionsAsync(
        DateOnly? since, DateOnly? until, ClaimsPrincipal principal,
        GetExceptionReportHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, since, until, ct));
    }

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El token no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}
