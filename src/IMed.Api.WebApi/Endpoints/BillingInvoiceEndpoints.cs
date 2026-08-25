using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Facturas y timbrado (FAC-5, pantallas B8 y B9). Bajo <c>/api/billing/invoices</c>.
///
/// <para>Emitir es de quien cobra; anular es del jefe administrativo. La diferencia importa: una
/// anulación consume un correlativo que después hay que explicar ante una auditoría.</para>
/// </summary>
public static class BillingInvoiceEndpoints
{
    public static IEndpointRouteBuilder MapBillingInvoiceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/invoices")
            .WithTags("Billing · Facturas")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapPost("/", IssueAsync)
            .WithName("IssueInvoice")
            .WithSummary("Emite y timbra una factura con los cargos pendientes de una cuenta.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        group.MapGet("/{invoiceId:guid}", GetAsync)
            .WithName("GetInvoice")
            .WithSummary("Factura con sus líneas y su documento fiscal.");

        group.MapGet("/by-account/{accountId:guid}", ListByAccountAsync)
            .WithName("ListAccountInvoices")
            .WithSummary("Facturas de una cuenta.");

        group.MapGet("/{invoiceId:guid}/pdf", PdfAsync)
            .WithName("GetInvoicePdf")
            .WithSummary("PDF de la factura. `format=thermal80` para ticket; `reprint=true` lo marca como copia.");

        group.MapPost("/{invoiceId:guid}/void", VoidAsync)
            .WithName("VoidInvoice")
            .WithSummary("Anula una factura dentro de ventana. El correlativo queda consumido.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> IssueAsync(
        IssueInvoiceRequest request, ClaimsPrincipal principal,
        IssueInvoiceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        var resultado = await handler.HandleAsync(new IssueInvoiceCommand(
            orgId.Value, request.AccountId, request.LocationId, request.DocumentTypeId,
            request.FiscalAuthorizationId, request.BillToPartyId,
            request.BuyerTaxId, request.BuyerName), ct);

        if (resultado.Rejected is { } r)
        {
            // 409 y no 400: la solicitud está bien formada, lo que falla es el estado del negocio
            // —no hay correlativos, la autorización venció, falta el RTN—. El código va aparte del
            // mensaje para que la pantalla pueda mandar al usuario al lugar que corresponde.
            return Results.Problem(
                title: "No se pudo emitir la factura",
                detail: r.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = r.Code });
        }

        return Results.Created($"/api/billing/invoices/{resultado.Invoice!.Id}", resultado.Invoice);
    }

    private static async Task<IResult> GetAsync(
        Guid invoiceId, ClaimsPrincipal principal, GetInvoiceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var factura = await handler.HandleAsync(orgId.Value, invoiceId, ct);
        return factura is null ? Results.NotFound() : Results.Ok(factura);
    }

    private static async Task<IResult> ListByAccountAsync(
        Guid accountId, ClaimsPrincipal principal,
        ListAccountInvoicesHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(orgId.Value, accountId, ct));
    }

    private static async Task<IResult> PdfAsync(
        Guid invoiceId, string? format, bool? reprint, ClaimsPrincipal principal,
        GetInvoicePdfHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        var plantilla = string.Equals(format, "thermal80", StringComparison.OrdinalIgnoreCase)
            ? InvoicePrintFormat.Thermal80
            : InvoicePrintFormat.Letter;

        var pdf = await handler.HandleAsync(orgId.Value, invoiceId, plantilla, reprint ?? false, ct);
        return pdf is null
            ? Results.NotFound()
            : Results.File(pdf.Bytes, "application/pdf", pdf.FileName);
    }

    private static async Task<IResult> VoidAsync(
        Guid invoiceId, VoidInvoiceRequest request, ClaimsPrincipal principal,
        VoidInvoiceHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        var resultado = await handler.HandleAsync(orgId.Value, invoiceId, request.Reason, ct);
        if (resultado.Rejected is { } r)
        {
            return Results.Problem(
                title: "No se pudo anular la factura",
                detail: r.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = r.Code });
        }
        return Results.Ok(resultado.Invoice);
    }

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El token no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

public sealed record IssueInvoiceRequest(
    Guid AccountId,
    Guid LocationId,
    Guid DocumentTypeId,
    Guid? FiscalAuthorizationId,
    Guid? BillToPartyId,
    string? BuyerTaxId,
    string? BuyerName);

public sealed record VoidInvoiceRequest(string Reason);
