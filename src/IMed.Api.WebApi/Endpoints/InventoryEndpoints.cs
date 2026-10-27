using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Inventory;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Inventario (plan de salida a producción, B1–B3 y B7). Bajo <c>/api/inventory</c>.
///
/// <para>Leer pide <c>inventory.read</c>; registrar entradas, traslados, consumos, ajustes y
/// conteos pide <c>inventory.operate</c>; configurar artículos, bodegas y proveedores, aprobar
/// ajustes, anular entradas y ver todas las sedes pide <c>inventory.manage</c>.</para>
/// </summary>
public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/inventory")
            .WithTags("Inventario")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.InventoryRead));
        var operar = PermissionPolicy.For(PermissionCatalog.InventoryOperate);
        var administrar = PermissionPolicy.For(PermissionCatalog.InventoryManage);

        g.MapGet("/settings", GetSettingsAsync).WithSummary("Umbral de aprobación de ajustes y días de alerta de vencimiento.");
        g.MapPut("/settings", PutSettingsAsync).RequireAuthorization(administrar);
        g.MapGet("/tax-codes", TaxCodesAsync)
            .WithSummary("Tributos activos del régimen fiscal, para elegir con cuál se vende un artículo.");

        g.MapGet("/items", SearchItemsAsync).WithSummary("Busca artículos por nombre, código, código de barras o ATC.");
        g.MapGet("/items/{id:guid}", GetItemAsync);
        g.MapPost("/items", CreateItemAsync).RequireAuthorization(administrar)
            .WithSummary("Crea un artículo. Si es vendible, asegura su cobrable \"sin precio\".");
        g.MapPut("/items/{id:guid}", UpdateItemAsync).RequireAuthorization(administrar);

        g.MapGet("/warehouses", ListWarehousesAsync).WithSummary("Bodegas; asegura la bodega general de cada sede.");
        g.MapPost("/warehouses", CreateWarehouseAsync).RequireAuthorization(administrar);
        g.MapPut("/warehouses/{id:guid}", UpdateWarehouseAsync).RequireAuthorization(administrar);

        g.MapGet("/suppliers", ListSuppliersAsync);
        g.MapPost("/suppliers", CreateSupplierAsync).RequireAuthorization(administrar);
        g.MapPut("/suppliers/{id:guid}", UpdateSupplierAsync).RequireAuthorization(administrar);

        g.MapGet("/stock", StockAsync).WithSummary("Existencias por bodega y lote, valorizadas al promedio.");
        g.MapGet("/kardex", KardexAsync).WithSummary("Movimientos de un artículo en una bodega, con saldo y costo corridos.");
        g.MapGet("/fefo", FefoAsync).WithSummary("Qué lotes saldrían (FEFO) para una cantidad, sin mover nada.");
        g.MapGet("/alerts", AlertsAsync).WithSummary("Vencidos, por vencer, bajo el mínimo y ajustes por aprobar de la sede activa.");
        g.MapGet("/documents/{kind}", DocumentsAsync).WithSummary("receipts, transfers, consumptions, adjustments o counts.");

        g.MapPost("/receipts", CreateReceiptAsync).RequireAuthorization(operar);
        g.MapGet("/receipts/{id:guid}", GetReceiptAsync);
        g.MapPost("/receipts/{id:guid}/void", VoidReceiptAsync).RequireAuthorization(administrar);

        g.MapPost("/transfers", CreateTransferAsync).RequireAuthorization(operar);
        g.MapGet("/transfers/{id:guid}", GetTransferAsync);

        g.MapPost("/consumptions", CreateConsumptionAsync).RequireAuthorization(operar);
        g.MapGet("/consumptions/{id:guid}", GetConsumptionAsync);

        g.MapPost("/adjustments", CreateAdjustmentAsync).RequireAuthorization(operar);
        g.MapGet("/adjustments/{id:guid}", GetAdjustmentAsync);
        g.MapPost("/adjustments/{id:guid}/approve", ApproveAdjustmentAsync).RequireAuthorization(administrar);
        g.MapPost("/adjustments/{id:guid}/reject", RejectAdjustmentAsync).RequireAuthorization(administrar);

        g.MapPost("/counts", StartCountAsync).RequireAuthorization(operar);
        g.MapGet("/counts/{id:guid}", GetCountAsync);
        g.MapPut("/counts/{id:guid}/lines", RecordCountAsync).RequireAuthorization(operar);
        g.MapPost("/counts/{id:guid}/lines", AddCountLineAsync).RequireAuthorization(operar);
        g.MapPost("/counts/{id:guid}/close", CloseCountAsync).RequireAuthorization(operar);
        g.MapPost("/counts/{id:guid}/cancel", CancelCountAsync).RequireAuthorization(operar);

        g.MapGet("/reports/valuation", ValuationAsync);
        g.MapGet("/reports/expiring", ExpiringAsync);
        g.MapGet("/reports/movements", MovementsAsync);
        g.MapGet("/reports/consumption", ConsumptionReportAsync);
        return app;
    }

    // ── Configuración ──

    private static Task<IResult> GetSettingsAsync(ClaimsPrincipal p, InventorySettingsHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.GetAsync(org, ct)));

    private static Task<IResult> PutSettingsAsync(
        InventorySettingsDto body, ClaimsPrincipal p, InventorySettingsHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.UpdateAsync(org, body, ct)));

    private static Task<IResult> TaxCodesAsync(ClaimsPrincipal p, IFiscalRepository fiscal, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok((await fiscal.ListTaxCodesAsync(org, ct))
            .Where(t => t.Active)
            .OrderBy(t => t.Rate)
            .Select(t => new { t.Id, t.Code, t.Name, t.Rate })
            .ToList()));

    // ── Artículos ──

    private static Task<IResult> SearchItemsAsync(
        string? q, string? kind, bool? otc, bool? activeOnly, int? limit, ClaimsPrincipal p,
        SearchStockItemsHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
            Results.Ok(await h.HandleAsync(org, q, kind, otc, activeOnly ?? false, limit ?? 200, ct)));

    private static Task<IResult> GetItemAsync(Guid id, ClaimsPrincipal p, StockItemCommandHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.GetAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> CreateItemAsync(
        StockItemInput body, ClaimsPrincipal p, StockItemCommandHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var dto = await h.CreateAsync(org, body, ct);
            return Results.Created($"/api/inventory/items/{dto.Id}", dto);
        });

    private static Task<IResult> UpdateItemAsync(
        Guid id, StockItemInput body, ClaimsPrincipal p, StockItemCommandHandler h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.UpdateAsync(org, id, body, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    // ── Bodegas y proveedores ──

    private static Task<IResult> ListWarehousesAsync(
        Guid? locationId, bool? activeOnly, ClaimsPrincipal p, WarehouseHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.ListAsync(org, locationId, activeOnly ?? false, ct)));

    private static Task<IResult> CreateWarehouseAsync(
        WarehouseInput body, ClaimsPrincipal p, WarehouseHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var dto = await h.CreateAsync(org, EndpointRun.User(p), body, ct);
            return Results.Created($"/api/inventory/warehouses/{dto.Id}", dto);
        });

    private static Task<IResult> UpdateWarehouseAsync(
        Guid id, WarehouseInput body, ClaimsPrincipal p, WarehouseHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.UpdateAsync(org, id, body, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> ListSuppliersAsync(
        string? q, bool? activeOnly, ClaimsPrincipal p, SupplierHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.ListAsync(org, q, activeOnly ?? false, ct)));

    private static Task<IResult> CreateSupplierAsync(
        SupplierInput body, ClaimsPrincipal p, SupplierHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var dto = await h.CreateAsync(org, body, ct);
            return Results.Created($"/api/inventory/suppliers/{dto.Id}", dto);
        });

    private static Task<IResult> UpdateSupplierAsync(
        Guid id, SupplierInput body, ClaimsPrincipal p, SupplierHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.UpdateAsync(org, id, body, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    // ── Lecturas ──

    private static Task<IResult> StockAsync(
        Guid? warehouseId, Guid? itemId, string? q, bool? all, bool? includeEmpty, ClaimsPrincipal p,
        IPermissionResolver permisos, InventoryQueryHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.StockAsync(
                org, EndpointRun.User(p), warehouseId, itemId, q, all ?? false, includeEmpty ?? false, ct));
        });

    private static Task<IResult> KardexAsync(
        Guid warehouseId, Guid itemId, DateOnly? from, DateOnly? to, ClaimsPrincipal p, InventoryQueryHandlers h,
        CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.KardexAsync(org, EndpointRun.User(p), warehouseId, itemId, from, to, ct)));

    private static Task<IResult> FefoAsync(
        Guid itemId, decimal? quantity, Guid? warehouseId, bool? pharmacy, ClaimsPrincipal p, InventoryQueryHandlers h,
        CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.FefoAsync(
            org, EndpointRun.User(p), warehouseId, itemId, quantity ?? 0m, pharmacy ?? false, ct)));

    private static Task<IResult> AlertsAsync(ClaimsPrincipal p, InventoryQueryHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => Results.Ok(await h.AlertsAsync(org, EndpointRun.User(p), ct)));

    private static Task<IResult> DocumentsAsync(
        string kind, DateOnly? from, DateOnly? to, bool? all, ClaimsPrincipal p, IPermissionResolver permisos,
        InventoryQueryHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.DocumentsAsync(org, EndpointRun.User(p), kind, from, to, all ?? false, ct));
        });

    // ── Entradas ──

    private static Task<IResult> CreateReceiptAsync(
        GoodsReceiptInput body, ClaimsPrincipal p, GoodsReceiptHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var id = await h.CreateAsync(org, EndpointRun.User(p), body, ct);
            return Results.Created($"/api/inventory/receipts/{id}", await q.ReceiptAsync(org, id, ct));
        });

    private static Task<IResult> GetReceiptAsync(Guid id, ClaimsPrincipal p, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await q.ReceiptAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> VoidReceiptAsync(
        Guid id, ReasonRequest body, ClaimsPrincipal p, GoodsReceiptHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.VoidAsync(org, EndpointRun.User(p), id, body.Reason, ct)
            ? Results.Ok(await q.ReceiptAsync(org, id, ct))
            : Results.NotFound());

    // ── Traslados y consumos ──

    private static Task<IResult> CreateTransferAsync(
        TransferInput body, ClaimsPrincipal p, StockMovementHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var id = await h.TransferAsync(org, EndpointRun.User(p), body, ct);
            return Results.Created($"/api/inventory/transfers/{id}", await q.TransferAsync(org, id, ct));
        });

    private static Task<IResult> GetTransferAsync(Guid id, ClaimsPrincipal p, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await q.TransferAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> CreateConsumptionAsync(
        ConsumptionInput body, ClaimsPrincipal p, StockMovementHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var id = await h.ConsumeAsync(org, EndpointRun.User(p), body, ct);
            return Results.Created($"/api/inventory/consumptions/{id}", await q.ConsumptionAsync(org, id, ct));
        });

    private static Task<IResult> GetConsumptionAsync(Guid id, ClaimsPrincipal p, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await q.ConsumptionAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    // ── Ajustes ──

    private static Task<IResult> CreateAdjustmentAsync(
        AdjustmentInput body, ClaimsPrincipal p, IPermissionResolver permisos, AdjustmentHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var aprueba = await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct);
            var r = await h.CreateAsync(org, EndpointRun.User(p), aprueba, body, ct);
            return Results.Created($"/api/inventory/adjustments/{r.Id}", r);
        });

    private static Task<IResult> GetAdjustmentAsync(Guid id, ClaimsPrincipal p, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await q.AdjustmentAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> ApproveAdjustmentAsync(Guid id, ClaimsPrincipal p, AdjustmentHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.ApproveAsync(org, EndpointRun.User(p), id, ct) is { } r ? Results.Ok(r) : Results.NotFound());

    private static Task<IResult> RejectAdjustmentAsync(
        Guid id, ReasonRequest body, ClaimsPrincipal p, AdjustmentHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
            await h.RejectAsync(org, EndpointRun.User(p), id, body.Reason, ct) is { } r ? Results.Ok(r) : Results.NotFound());

    // ── Conteos ──

    private static Task<IResult> StartCountAsync(
        StartCountRequest body, ClaimsPrincipal p, StockCountHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var id = await h.StartAsync(org, EndpointRun.User(p), body.WarehouseId, body.Notes, ct);
            return Results.Created($"/api/inventory/counts/{id}", await q.CountAsync(org, id, ct));
        });

    private static Task<IResult> GetCountAsync(Guid id, ClaimsPrincipal p, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await q.CountAsync(org, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

    private static Task<IResult> RecordCountAsync(
        Guid id, CountRecordInput body, ClaimsPrincipal p, StockCountHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.RecordAsync(org, EndpointRun.User(p), id, body, ct)
            ? Results.Ok(await q.CountAsync(org, id, ct))
            : Results.NotFound());

    private static Task<IResult> AddCountLineAsync(
        Guid id, CountAddLineInput body, ClaimsPrincipal p, StockCountHandlers h, InventoryQueryHandlers q, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.AddLineAsync(org, EndpointRun.User(p), id, body, ct)
            ? Results.Ok(await q.CountAsync(org, id, ct))
            : Results.NotFound());

    private static Task<IResult> CloseCountAsync(
        Guid id, ClaimsPrincipal p, IPermissionResolver permisos, StockCountHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            var aprueba = await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct);
            return await h.CloseAsync(org, EndpointRun.User(p), aprueba, id, ct) is { } r ? Results.Ok(r) : Results.NotFound();
        });

    private static Task<IResult> CancelCountAsync(Guid id, ClaimsPrincipal p, StockCountHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org => await h.CancelAsync(org, EndpointRun.User(p), id, ct) ? Results.NoContent() : Results.NotFound());

    // ── Reportes (B7) ──

    private static Task<IResult> ValuationAsync(
        Guid? warehouseId, bool? all, ClaimsPrincipal p, IPermissionResolver permisos, InventoryReportHandlers h,
        CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.ValuationAsync(org, EndpointRun.User(p), warehouseId, all ?? false, ct));
        });

    private static Task<IResult> ExpiringAsync(
        int? days, bool? all, ClaimsPrincipal p, IPermissionResolver permisos, InventoryReportHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.ExpiringAsync(org, EndpointRun.User(p), days ?? InventorySettings.DefaultExpiryAlertDays, all ?? false, ct));
        });

    private static Task<IResult> MovementsAsync(
        DateOnly from, DateOnly to, Guid? warehouseId, Guid? itemId, string? type, bool? all, ClaimsPrincipal p,
        IPermissionResolver permisos, InventoryReportHandlers h, CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.MovementsAsync(org, EndpointRun.User(p), from, to, warehouseId, itemId, type, all ?? false, ct));
        });

    private static Task<IResult> ConsumptionReportAsync(
        DateOnly from, DateOnly to, bool? all, ClaimsPrincipal p, IPermissionResolver permisos, InventoryReportHandlers h,
        CancellationToken ct) =>
        EndpointRun.Org(p, async org =>
        {
            if (all == true && !await EndpointRun.HasAsync(p, permisos, org, PermissionCatalog.InventoryManage, ct))
            {
                return EndpointRun.Forbidden("Ver todas las sedes requiere el permiso de gerente de inventario.");
            }
            return Results.Ok(await h.ConsumptionAsync(org, EndpointRun.User(p), from, to, all ?? false, ct));
        });
}

public sealed record ReasonRequest(string Reason);

public sealed record StartCountRequest(Guid? WarehouseId, string? Notes);

/// <summary>
/// Plomería común de los endpoints de inventario y farmacia: organización del token, usuario, y
/// la traducción de los errores esperados a respuestas que el usuario puede leer.
/// </summary>
internal static class EndpointRun
{
    public static async Task<IResult> Org(ClaimsPrincipal principal, Func<Guid, Task<IResult>> action)
    {
        if (!Guid.TryParse(principal.FindFirstValue("organization_id"), out var org))
        {
            return Results.Problem(
                title: "Sin organización", detail: "El token no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            return await action(org);
        }
        catch (InsufficientStockException ex)
        {
            return Results.Problem(title: "Sin existencia suficiente", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "No se puede hacer", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    public static Guid? User(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }

    public static async Task<bool> HasAsync(
        ClaimsPrincipal principal, IPermissionResolver resolver, Guid org, string permission, CancellationToken ct)
    {
        var roles = principal.FindAll("roles").Select(c => c.Value).ToList();
        return roles.Count > 0 && (await resolver.ResolveAsync(org, roles, ct)).Has(permission);
    }

    public static IResult Forbidden(string detail) =>
        Results.Problem(title: "Sin permiso", detail: detail, statusCode: StatusCodes.Status403Forbidden);
}
