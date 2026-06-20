using System.Security.Claims;
using IMed.Api.Application.LabReception;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints de recepción de laboratorio (Épica LIS-3, L3-F2). Punto de entrada
/// unificado: resolver orden interna, capturar orden externa, y recibir la
/// muestra (con enrutamiento + precondiciones). Política <c>LabTechnician</c>.
/// </summary>
public static class LabReceptionEndpoints
{
    public static IEndpointRouteBuilder MapLabReceptionEndpoints(this IEndpointRouteBuilder app)
    {
        // Baseline lectura (LabRead); las escrituras exigen LabTechnician.
        var group = app.MapGroup("/api/lab/reception")
            .WithTags("LabReception")
            .RequireAuthorization("LabRead");

        group.MapGet("/order/{serviceRequestId}", ResolveAsync)
            .WithName("ResolveReceptionOrder")
            .WithSummary("Resuelve una orden interna: paciente, examen, enrutamiento y precondiciones.");

        group.MapPost("/external-order", CreateExternalAsync)
            .WithName("CreateExternalOrder")
            .WithSummary("Crea una orden externa (médico externo + ServiceRequest) para un paciente ya resuelto.")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/receive", ReceiveAsync)
            .WithName("ReceiveSpecimen")
            .WithSummary("Recibe la muestra: crea Specimen/accession/intake o bloquea por precondición.")
            .RequireAuthorization("LabTechnician");

        return app;
    }

    private static async Task<IResult> ResolveAsync(
        string serviceRequestId, ClaimsPrincipal principal, ResolveReceptionOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, serviceRequestId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateExternalAsync(
        CreateExternalOrderRequest request, ClaimsPrincipal principal, CreateExternalOrderHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new CreateExternalOrderCommand(
                orgId.Value, request.PatientFhirId, request.RequesterName, request.RequesterIdentifier,
                request.LoincCode, request.CodeText, request.Priority, request.OccurrenceDateTime,
                request.ReasonText, request.Note), ct);
            return Results.Created($"/api/lab/reception/order/{result.ServiceRequestId}", result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ReceiveAsync(
        ReceiveRequest request, ClaimsPrincipal principal, ReceiveSpecimenHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(new ReceiveCommand(
                orgId.Value, userId.Value, request.ServiceRequestId, request.FastingSatisfied,
                request.IsWalkIn, request.ScheduledFor, request.TypeText, request.CollectedDateTime,
                request.Note), ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Forbidden() =>
        Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

public sealed record CreateExternalOrderRequest(
    string PatientFhirId,
    string RequesterName,
    string? RequesterIdentifier,
    string? LoincCode,
    string CodeText,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note);

public sealed record ReceiveRequest(
    string ServiceRequestId,
    bool? FastingSatisfied,
    bool IsWalkIn,
    DateTimeOffset? ScheduledFor,
    string? TypeText,
    string? CollectedDateTime,
    string? Note);
