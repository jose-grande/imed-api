using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Locations;
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

        group.MapPost("/{invoiceId:guid}/credit-notes", IssueCreditNoteAsync)
            .WithName("IssueCreditNote")
            .WithSummary("Emite una nota de crédito total (sin líneas) o por líneas y cantidades.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapGet("/{invoiceId:guid}/credit-notes", ListCreditNotesAsync)
            .WithName("ListCreditNotes")
            .WithSummary("Notas de crédito emitidas contra la factura.");

        app.MapPost("/api/billing/credit-notes/{creditNoteId:guid}/refund", RefundCreditNoteAsync)
            .WithTags("Billing · Facturas")
            .WithName("RefundCreditNote")
            .WithSummary("Paga la devolución de una nota de crédito desde el turno de caja abierto.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCollect));

        return app;
    }

    private static async Task<IResult> IssueCreditNoteAsync(
        Guid invoiceId, IssueCreditNoteRequest request, ClaimsPrincipal principal,
        IssueCreditNoteHandler handler, CancellationToken ct)
    {
        if (OrgAndUser(principal) is not { } ids)
        {
            return Results.Forbid();
        }
        var (org, user) = ids;
        var r = await handler.HandleAsync(new IssueCreditNoteCommand(
            org, invoiceId, request.Reason ?? string.Empty, user,
            request.Lines?.Select(l => new CreditNoteLineRequest(l.InvoiceLineId, l.Quantity)).ToList()), ct);
        return r.Succeeded
            ? Results.Ok(r.CreditNote)
            : Results.Problem(
                title: "No se pudo emitir la nota de crédito", detail: r.Rejected!.Message,
                statusCode: r.Rejected.Code == "factura_inexistente" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = r.Rejected.Code });
    }

    private static async Task<IResult> ListCreditNotesAsync(
        Guid invoiceId, ClaimsPrincipal principal, ListCreditNotesHandler handler, CancellationToken ct) =>
        OrgAndUser(principal) is { } ids
            ? Results.Ok(await handler.HandleAsync(ids.Org, invoiceId, ct))
            : Results.Forbid();

    private static async Task<IResult> RefundCreditNoteAsync(
        Guid creditNoteId, ClaimsPrincipal principal, RefundCreditNoteHandler handler, CancellationToken ct)
    {
        if (OrgAndUser(principal) is not { } ids)
        {
            return Results.Forbid();
        }
        var (nota, error) = await handler.HandleAsync(ids.Org, creditNoteId, ids.User, ct);
        return nota is not null
            ? Results.Ok(nota)
            : Results.Problem(title: "No se pudo registrar la devolución", detail: error, statusCode: StatusCodes.Status409Conflict);
    }

    private static (Guid Org, Guid User)? OrgAndUser(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(principal.FindFirstValue("organization_id"), out var org) && Guid.TryParse(sub, out var user)
            ? (org, user) : null;
    }

    private static async Task<IResult> IssueAsync(
        IssueInvoiceRequest request, ClaimsPrincipal principal,
        IssueInvoiceHandler handler, ResolveActiveLocationHandler activeLocation, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        // La sede de emisión no se acepta a ciegas del cuerpo (FAC-9, D4): es la sede activa del
        // usuario, y si pide otra tiene que estar asignado a ella. Con eso nadie emite con el CAI
        // de una sede donde no trabaja.
        var userId = ParseSub(principal);
        Guid sede;
        try
        {
            sede = await activeLocation.HandleAsync(orgId.Value, userId, request.LocationId, ct);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Sede inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var resultado = await handler.HandleAsync(new IssueInvoiceCommand(
            orgId.Value, request.AccountId, sede, request.DocumentTypeId,
            request.FiscalAuthorizationId, request.BillToPartyId,
            request.BuyerTaxId, request.BuyerName, userId), ct);

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

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

/// <param name="LocationId">Opcional. Sin ella se emite en la sede activa del usuario.</param>
public sealed record IssueInvoiceRequest(
    Guid AccountId,
    Guid? LocationId,
    Guid DocumentTypeId,
    Guid? FiscalAuthorizationId,
    Guid? BillToPartyId,
    string? BuyerTaxId,
    string? BuyerName);

public sealed record VoidInvoiceRequest(string Reason);

public sealed record IssueCreditNoteRequest(string? Reason, IReadOnlyList<CreditNoteLineInput>? Lines);

public sealed record CreditNoteLineInput(Guid InvoiceLineId, decimal Quantity);
