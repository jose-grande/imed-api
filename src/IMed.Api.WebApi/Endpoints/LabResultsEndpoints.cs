using System.Security.Claims;
using IMed.Api.Application.LabResults;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Captura de resultados de laboratorio (Épica LIS-4, F2). Política
/// <c>LabValidator</c> (bioanalista/patólogo/supervisor) — quien ingresa/valida.
/// </summary>
public static class LabResultsEndpoints
{
    public static IEndpointRouteBuilder MapLabResultsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/lab/results", EnterAsync)
            .WithTags("LabResults")
            .WithName("EnterLabResults")
            .WithSummary("Captura resultados → Observations (con flags) + DiagnosticReport (preliminary).")
            .RequireAuthorization("LabValidator");

        app.MapGet("/api/lab/results/entry-sheet/{serviceRequestId}", EntrySheetAsync)
            .WithTags("LabResults")
            .WithName("GetResultEntrySheet")
            .WithSummary("Contexto de captura de una orden: paciente, analitos del panel expandidos, unidad, rango del paciente y valor previo (LIS-4b).")
            .RequireAuthorization("LabRead");

        app.MapPost("/api/lab/results/observations/{observationId}/correct", CorrectAsync)
            .WithTags("LabResults")
            .WithName("CorrectLabResult")
            .WithSummary("Corrige un resultado aún no liberado, con motivo. Recalcula el flag y versiona el recurso (LIS-4b).")
            .RequireAuthorization("LabValidator");

        app.MapGet("/api/lab/results/by-patient/{patientId}", ByPatientAsync)
            .WithTags("LabResults")
            .WithName("GetLabResultsByPatient")
            .WithSummary("Lista los informes (DiagnosticReport) de un paciente.")
            .RequireAuthorization("LabRead");

        app.MapGet("/api/lab/results/pending", PendingAsync)
            .WithTags("LabResults")
            .WithName("GetPendingValidation")
            .WithSummary("Informes preliminary pendientes de validación (LIS-6).")
            .RequireAuthorization("LabRead");

        app.MapGet("/api/lab/results/{reportId}/history", HistoryAsync)
            .WithTags("LabResults")
            .WithName("GetReportHistory")
            .WithSummary("Tendencia (valores previos) del LOINC del informe.")
            .RequireAuthorization("LabRead");

        app.MapGet("/api/lab/results/{reportId}", DetailAsync)
            .WithTags("LabResults")
            .WithName("GetLabResultDetail")
            .WithSummary("Detalle de un informe con sus observaciones.")
            .RequireAuthorization("LabRead");

        app.MapPost("/api/lab/results/{reportId}/release", ReleaseAsync)
            .WithTags("LabResults")
            .WithName("ReleaseLabResult")
            .WithSummary("Valida y libera un informe → final + Provenance (LIS-6).")
            .RequireAuthorization("LabValidator");

        return app;
    }

    private static async Task<IResult> PendingAsync(
        ClaimsPrincipal principal, GetPendingValidationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        return Results.Ok(await handler.HandleAsync(new GetPendingValidationQuery(orgId.Value), ct));
    }

    private static async Task<IResult> HistoryAsync(
        string reportId, ClaimsPrincipal principal, GetReportHistoryHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        return Results.Ok(await handler.HandleAsync(new GetReportHistoryQuery(orgId.Value, reportId), ct));
    }

    private static async Task<IResult> ReleaseAsync(
        string reportId, ClaimsPrincipal principal, ReleaseReportHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        var userRaw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var userId = Guid.TryParse(userRaw, out var uid) ? uid : (Guid?)null;
        if (orgId is null || userId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new ReleaseReportCommand(orgId.Value, userId.Value, reportId), ct));
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> ByPatientAsync(
        string patientId, ClaimsPrincipal principal, GetResultsByPatientHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new GetResultsByPatientQuery(orgId.Value, patientId), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> DetailAsync(
        string reportId, ClaimsPrincipal principal, GetResultDetailHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new GetResultDetailQuery(orgId.Value, reportId), ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> EntrySheetAsync(
        string serviceRequestId, ClaimsPrincipal principal,
        GetResultEntrySheetHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        var sheet = await handler.HandleAsync(orgId.Value, serviceRequestId, ct);
        return sheet is null ? Results.NotFound() : Results.Ok(sheet);
    }

    private static async Task<IResult> CorrectAsync(
        string observationId, CorrectResultRequest request, ClaimsPrincipal principal,
        CorrectResultHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        var userRaw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var userId = Guid.TryParse(userRaw, out var uid) ? uid : (Guid?)null;
        if (orgId is null || userId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            var result = await handler.HandleAsync(new CorrectResultCommand(
                orgId.Value, userId.Value, observationId,
                request.ValueNumeric, request.ValueString, request.Unit, request.Reason), ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static Guid? ParseOrg(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static async Task<IResult> EnterAsync(
        EnterResultsRequest request, ClaimsPrincipal principal, EnterResultsHandler handler, CancellationToken ct)
    {
        var orgId = Guid.TryParse(principal.FindFirstValue("organization_id"), out var oid) ? oid : (Guid?)null;
        var userRaw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        var userId = Guid.TryParse(userRaw, out var uid) ? uid : (Guid?)null;
        if (orgId is null || userId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            var result = await handler.HandleAsync(new EnterResultsCommand(
                orgId.Value, userId.Value, request.ServiceRequestId, request.ExternalLabName,
                request.Results, request.Conclusion), ct);
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
}

public sealed record EnterResultsRequest(
    string ServiceRequestId,
    string? ExternalLabName,
    IReadOnlyList<ResultLine> Results,
    string? Conclusion);

/// <summary>Corrección de un resultado no liberado. El motivo es obligatorio (LIS-4b).</summary>
public sealed record CorrectResultRequest(
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit,
    string Reason);
