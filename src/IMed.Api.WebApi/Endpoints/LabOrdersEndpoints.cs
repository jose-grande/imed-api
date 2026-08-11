using System.Security.Claims;
using IMed.Api.Application.LabOrders;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints relacionados con órdenes de laboratorio (Épica L5).
/// <list type="bullet">
///   <item><c>GET /api/lab-orders/{id}/pdf</c> — descarga la orden como PDF.</item>
/// </list>
/// La creación/edición sigue hablando con HAPI desde el frontend (vertical
/// slice del piloto).
/// </summary>
public static class LabOrdersEndpoints
{
    public static IEndpointRouteBuilder MapLabOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab-orders")
            .WithTags("LabOrders")
            .RequireAuthorization();

        group.MapGet("/{id}/pdf", DownloadPdfAsync)
            .WithName("DownloadLabOrderPdf")
            .WithSummary("Devuelve el PDF de un ServiceRequest (orden de 1 examen / legacy).");

        group.MapGet("/group/{requisition}/pdf", DownloadGroupPdfAsync)
            .WithName("DownloadLabOrderGroupPdf")
            .WithSummary("Devuelve el PDF de una orden de laboratorio (todos los exámenes que comparten requisition).");

        // --- Gateway de órdenes del médico (LIS-RD-09): el frontend ya no escribe a HAPI directo. ---
        group.MapPost("/allocate-number", AllocateNumberAsync).WithName("AllocateLabOrderNumber")
            .WithSummary("Asigna un número de orden correlativo legible (F1).");
        group.MapPost("", CreateAsync).WithName("CreateLabOrder")
            .WithSummary("Crea una orden de laboratorio (vía gateway).");
        group.MapPut("/{id}", UpdateAsync).WithName("UpdateLabOrder")
            .WithSummary("Actualiza una orden (promover draft→active, revocar, etc.).");
        group.MapGet("/{id}", GetAsync).WithName("GetLabOrder");
        group.MapGet("/by-encounter/{encounterId}", ByEncounterAsync).WithName("ListLabOrdersByEncounter");
        group.MapGet("/by-patient/{patientId}", ByPatientAsync).WithName("ListLabOrdersByPatient");

        return app;
    }

    private static async Task<IResult> AllocateNumberAsync(
        ClaimsPrincipal principal, AllocateOrderNumberHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        // El número de orden es por sede: se pasa el usuario para resolver en cuál está parado.
        var userId = Guid.TryParse(
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"),
            out var sub) ? sub : (Guid?)null;
        var number = await handler.HandleAsync(orgId.Value, userId, ct);
        return Results.Ok(new { requisition = number });
    }

    private static async Task<IResult> CreateAsync(
        CreateLabOrderApiRequest request, ClaimsPrincipal principal, UpsertLabOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        try
        {
            var dto = await handler.HandleAsync(ToCommand(orgId.Value, null, request), ct);
            return Results.Created($"/api/lab-orders/{dto.Id}", dto);
        }
        catch (ArgumentException ex) { return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
    }

    private static async Task<IResult> UpdateAsync(
        string id, CreateLabOrderApiRequest request, ClaimsPrincipal principal, UpsertLabOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        try
        {
            var dto = await handler.HandleAsync(ToCommand(orgId.Value, id, request), ct);
            return Results.Ok(dto);
        }
        catch (ArgumentException ex) { return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest); }
    }

    private static async Task<IResult> GetAsync(
        string id, ClaimsPrincipal principal, GetLabOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        var dto = await handler.HandleAsync(orgId.Value, id, ct);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> ByEncounterAsync(
        string encounterId, ClaimsPrincipal principal, ListLabOrdersByEncounterHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        return Results.Ok(await handler.HandleAsync(orgId.Value, encounterId, ct));
    }

    private static async Task<IResult> ByPatientAsync(
        string patientId, ClaimsPrincipal principal, ListLabOrdersByPatientHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        return Results.Ok(await handler.HandleAsync(orgId.Value, patientId, ct));
    }

    private static UpsertLabOrderCommand ToCommand(Guid orgId, string? id, CreateLabOrderApiRequest r) =>
        new(orgId, id, r.PatientFhirId, r.EncounterFhirId, r.RequesterPractitionerId, r.Status, r.Priority,
            r.LoincCode, r.LocalCode, r.LocalSystem, r.CodeText, r.CodeDisplay, r.OccurrenceDateTime,
            r.ReasonText, r.Note, r.Requisition);

    private static Guid? ParseOrg(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static IResult ForbiddenOrg() =>
        Results.Problem(title: "Sin organización", detail: "El JWT no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> DownloadPdfAsync(
        string id,
        ClaimsPrincipal principal,
        GetLabOrderPdfHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Problem(
                title: "id requerido",
                detail: "Falta el id del ServiceRequest.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var orgRaw = principal.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var orgId))
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(
            new GetLabOrderPdfQuery(orgId, id), cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
    }

    private static async Task<IResult> DownloadGroupPdfAsync(
        string requisition,
        ClaimsPrincipal principal,
        GetLabOrderPdfHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(requisition))
        {
            return Results.Problem(
                title: "requisition requerida",
                detail: "Falta el identificador de la orden.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var orgRaw = principal.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var orgId))
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleGroupAsync(
            new GetLabOrderGroupPdfQuery(orgId, requisition), cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
    }
}

public sealed record CreateLabOrderApiRequest(
    string PatientFhirId,
    string? EncounterFhirId,
    string? RequesterPractitionerId,
    string Status,
    string? Priority,
    string? LoincCode,
    string? LocalCode,
    string? LocalSystem,
    string CodeText,
    string? CodeDisplay,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    string? Requisition);
