using IMed.Api.Application.Abstractions;
using System.Security.Claims;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Cuentas y cargos (FAC-4, pantalla B6). Bajo <c>/api/billing/accounts</c>.
/// </summary>
public static class BillingAccountEndpoints
{
    public static IEndpointRouteBuilder MapBillingAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing/accounts")
            .WithTags("Billing · Cuentas")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        group.MapGet("/", SearchAsync)
            .WithName("SearchBillingAccounts")
            .WithSummary("Busca cuentas con su situación de cobro: por facturar, por cobrar, al día.");

        group.MapPost("/", OpenAsync)
            .WithName("OpenBillingAccount")
            .WithSummary("Abre una cuenta, o devuelve la del encuentro si ya existe.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCharge));

        group.MapGet("/{accountId:guid}/checkout", GetCheckoutAsync)
            .WithName("GetAccountCheckout")
            .WithSummary("Lo que el paciente debe: por facturar y facturas con saldo, y si se puede cobrar acá.");

        group.MapPost("/{accountId:guid}/checkout", CheckoutAsync)
            .WithName("AccountCheckout")
            .WithSummary("Factura lo pendiente y cobra. Con pagos pide billing.collect; sin pagos (facturar sin cobrar), billing.invoice.");

        group.MapGet("/{accountId:guid}", StatementAsync)
            .WithName("GetAccountStatement")
            .WithSummary("Estado de cuenta: cargos, descuentos y totales.");

        group.MapPost("/{accountId:guid}/close", CloseAsync)
            .WithName("CloseBillingAccount")
            .WithSummary("Cierra la cuenta.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCharge));

        group.MapPost("/{accountId:guid}/charges", CaptureAsync)
            .WithName("CaptureCharge")
            .WithSummary("Captura un cargo. Idempotente por origen.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingCharge));

        group.MapPost("/charges/{chargeId:guid}/reverse", ReverseAsync)
            .WithName("ReverseCharge")
            .WithSummary("Reversa un cargo con motivo. No lo borra.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> SearchAsync(
        string? patientFhirId, string? status, string? situation, string? origin, string? q,
        Guid? locationId, DateOnly? from, DateOnly? to, int? limit, bool? consultation, ClaimsPrincipal principal,
        SearchAccountsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null
            ? Forbidden()
            : Results.Ok(await handler.HandleAsync(new SearchAccountsQuery(
                orgId.Value, patientFhirId, status, situation, origin, q, locationId, from, to, limit ?? 50,
                WithConsultationStatus: consultation == true), ct));
    }

    private static Task<IResult> GetCheckoutAsync(
        Guid accountId, ClaimsPrincipal principal, AccountCheckoutHandler handler, CancellationToken ct) =>
        EndpointRun.Org(principal, async org => EndpointRun.User(principal) is not { } user
            ? EndpointRun.Forbidden("El token no trae usuario.")
            : await handler.GetAsync(org, user, accountId, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    /// <summary>
    /// Con pagos es cobrar (<c>billing.collect</c>); sin pagos es facturar sin cobrar
    /// (<c>billing.invoice</c>, D2). Quien cobra emite al cobrar sin necesitar el segundo.
    /// </summary>
    private static Task<IResult> CheckoutAsync(
        Guid accountId, AccountCheckoutInput body, ClaimsPrincipal principal,
        AccountCheckoutHandler handler, IPermissionResolver permisos, CancellationToken ct) =>
        EndpointRun.Org(principal, async org =>
        {
            if (EndpointRun.User(principal) is not { } user)
            {
                return EndpointRun.Forbidden("El token no trae usuario.");
            }
            var cobra = body.Tenders is { Count: > 0 };
            var permiso = cobra ? PermissionCatalog.BillingCollect : PermissionCatalog.BillingInvoice;
            if (!await EndpointRun.HasAsync(principal, permisos, org, permiso, ct))
            {
                return EndpointRun.Forbidden(cobra
                    ? "Cobrar pide el permiso de cobro (cajero)."
                    : "Facturar sin cobrar pide el permiso de facturar.");
            }
            return await handler.CheckoutAsync(org, user, accountId, body, ct) is { } r
                ? Results.Ok(r)
                : Results.NotFound();
        });

    private static async Task<IResult> OpenAsync(
        OpenAccountRequest request, ClaimsPrincipal principal,
        OpenAccountHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new OpenAccountCommand(
                orgId.Value, request.PartyId, request.PatientFhirId, request.PatientName,
                request.EncounterFhirId, request.Kind), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> StatementAsync(
        Guid accountId, ClaimsPrincipal principal,
        GetAccountStatementHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var estado = await handler.HandleAsync(orgId.Value, accountId, ct);
        return estado is null ? Results.NotFound() : Results.Ok(estado);
    }

    private static async Task<IResult> CloseAsync(
        Guid accountId, ClaimsPrincipal principal, CloseAccountHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return await handler.HandleAsync(orgId.Value, accountId, ct)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(
                title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> CaptureAsync(
        Guid accountId, CaptureChargeRequest request, ClaimsPrincipal principal,
        CaptureChargeHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }

        var sourceKind = Enum.TryParse<ChargeSourceKind>(request.SourceKind, ignoreCase: true, out var k)
            ? k : ChargeSourceKind.Manual;

        // Un cargo manual necesita un origen propio y único: sin eso, dos cargos manuales del
        // mismo ítem chocarían contra el índice de idempotencia, y agregar dos veces algo a mano
        // es legítimo.
        var sourceId = string.IsNullOrWhiteSpace(request.SourceId)
            ? (sourceKind == ChargeSourceKind.Manual ? Guid.CreateVersion7().ToString() : string.Empty)
            : request.SourceId;

        try
        {
            var resultado = await handler.HandleAsync(new CaptureChargeCommand(
                orgId.Value, accountId, request.BillableItemId, sourceKind, sourceId,
                request.Quantity ?? 1m, request.DiscountAmount ?? 0m,
                RequestedByUserId: ParseSub(principal),
                DiscountAuthorizedBy: request.DiscountAuthorizedBy,
                PerformedAt: request.PerformedAt,
                PerformerPractitionerId: request.PerformerPractitionerId,
                SourceFhirRef: request.SourceFhirRef,
                Reason: request.Reason,
                PayerContractId: request.PayerContractId), ct);

            if (resultado.Rejected is { } motivo)
            {
                return Results.Problem(
                    title: "No se pudo capturar el cargo",
                    detail: motivo,
                    statusCode: StatusCodes.Status409Conflict);
            }

            // 200 y no 201 cuando ya estaba cobrado: el recurso no se creó ahora, y el llamador
            // —el laboratorio admitiendo una orden— tiene que poder distinguirlo sin tratarlo
            // como error.
            return resultado.AlreadyCharged
                ? Results.Ok(resultado)
                : Results.Created($"/api/billing/accounts/{accountId}", resultado);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(
                title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ReverseAsync(
        Guid chargeId, ReverseChargeRequest request, ClaimsPrincipal principal,
        ReverseChargeHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var cargo = await handler.HandleAsync(orgId.Value, chargeId, request.Reason, ct);
            return cargo is null ? Results.NotFound() : Results.Ok(cargo);
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

    private static IResult Forbidden() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El token no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

public sealed record OpenAccountRequest(
    Guid? PartyId,
    string? PatientFhirId,
    string? PatientName,
    string? EncounterFhirId,
    string? Kind);

public sealed record CaptureChargeRequest(
    Guid BillableItemId,
    string? SourceKind,
    string? SourceId,
    decimal? Quantity,
    decimal? DiscountAmount,
    Guid? DiscountAuthorizedBy,
    DateTimeOffset? PerformedAt,
    string? PerformerPractitionerId,
    string? SourceFhirRef,
    string? Reason,
    Guid? PayerContractId);

public sealed record ReverseChargeRequest(string Reason);
