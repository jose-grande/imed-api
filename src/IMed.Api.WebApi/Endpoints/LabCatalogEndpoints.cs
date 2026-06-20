using System.Security.Claims;
using IMed.Api.Application.LabCatalog;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints del catálogo de laboratorio (Épica LIS-1, L1-F3): pruebas, paneles
/// (con constructor de versiones), rangos de referencia, ofertas por sede, y
/// lectura de fórmulas calculadas / TAT. Lo consume la UI admin de Laboratorio.
///
/// <para><b>Auth:</b> por ahora solo autenticación (como FormsEndpoints en Hito 1).
/// El RBAC fino (escritura limitada a <c>lab_supervisor</c>/<c>org_admin</c>) se
/// cablea en LIS-2, cuando los roles de laboratorio existan en Keycloak.</para>
/// </summary>
public static class LabCatalogEndpoints
{
    public static IEndpointRouteBuilder MapLabCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // Lectura: cualquier rol de lab + médico (LabRead). Escritura: supervisor/org_admin
        // (LabSupervisor) — RBAC fino de LIS-2 (L2-D4), cierra el follow-up de LIS-1.
        var group = app.MapGroup("/api/lab-catalog")
            .WithTags("LabCatalog")
            .RequireAuthorization("LabRead");

        // -------- Pruebas --------
        group.MapGet("/tests", SearchTestsAsync).WithName("SearchLabTests");
        group.MapGet("/tests/{id:guid}", GetTestByIdAsync).WithName("GetLabTestById");
        group.MapPost("/tests", CreateTestAsync).WithName("CreateLabTest").RequireAuthorization("LabSupervisor");
        group.MapPut("/tests/{id:guid}", UpdateTestAsync).WithName("UpdateLabTest").RequireAuthorization("LabSupervisor");
        group.MapPost("/tests/{id:guid}/active", SetTestActiveAsync).WithName("SetLabTestActive").RequireAuthorization("LabSupervisor");

        // -------- Paneles --------
        group.MapGet("/panels", ListPanelsAsync).WithName("ListLabPanels");
        group.MapGet("/panels/{id:guid}", GetPanelDetailAsync).WithName("GetLabPanelDetail");
        group.MapPost("/panels", CreatePanelAsync).WithName("CreateLabPanel").RequireAuthorization("LabSupervisor");
        group.MapPost("/panels/{id:guid}/versions", CreatePanelVersionAsync).WithName("CreateLabPanelVersion").RequireAuthorization("LabSupervisor");

        // -------- Rangos de referencia --------
        group.MapGet("/reference-ranges", ListRangesAsync).WithName("ListReferenceRanges");
        group.MapPost("/reference-ranges", CreateRangeAsync).WithName("CreateReferenceRange").RequireAuthorization("LabSupervisor");
        group.MapPut("/reference-ranges/{id:guid}", UpdateRangeAsync).WithName("UpdateReferenceRange").RequireAuthorization("LabSupervisor");

        // -------- Ofertas por sede --------
        group.MapPut("/offerings", SetOfferingAsync).WithName("SetLabTestOffering").RequireAuthorization("LabSupervisor");

        // -------- Fórmulas calculadas / TAT (read-only en LIS-1) --------
        group.MapGet("/derived-calculations", ListDerivedAsync).WithName("ListDerivedCalculations");
        group.MapGet("/tat-targets", ListTatAsync).WithName("ListTatTargets");

        return app;
    }

    // ------------------------------ Pruebas ------------------------------

    private static async Task<IResult> SearchTestsAsync(
        ClaimsPrincipal principal, SearchLabTestsHandler handler,
        [FromQuery] string? q, [FromQuery] string? department, [FromQuery] bool? activeOnly, [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        var result = await handler.HandleAsync(
            new SearchLabTestsQuery(orgId, q, department, activeOnly ?? true, limit ?? 50), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetTestByIdAsync(Guid id, GetLabTestByIdHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLabTestByIdQuery(id), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateTestAsync(
        CreateLabTestRequest request, ClaimsPrincipal principal, CreateLabTestHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new CreateLabTestCommand(
                ParseOrganizationId(principal), request.Code, request.Name, request.Datatype, request.LoincCode,
                request.Unit, request.Department, request.FastingHours, request.CollectionType, request.PatientInstruction),
                cancellationToken);
            return Results.Created($"/api/lab-catalog/tests/{result.Id}", result);
        }
        catch (InvalidOperationException ex) { return Conflict(ex); }
        catch (ArgumentException ex) { return BadRequest(ex); }
    }

    private static async Task<IResult> UpdateTestAsync(
        Guid id, UpdateLabTestRequest request, UpdateLabTestHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new UpdateLabTestCommand(
                id, request.Name, request.Datatype, request.LoincCode, request.Unit, request.Department,
                request.FastingHours, request.CollectionType, request.PatientInstruction), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex); }
    }

    private static async Task<IResult> SetTestActiveAsync(
        Guid id, SetActiveRequest request, SetLabTestActiveHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new SetLabTestActiveCommand(id, request.Active), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    // ------------------------------ Paneles ------------------------------

    private static async Task<IResult> ListPanelsAsync(ClaimsPrincipal principal, ListLabPanelsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListLabPanelsQuery(ParseOrganizationId(principal)), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetPanelDetailAsync(Guid id, GetLabPanelDetailHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetLabPanelDetailQuery(id), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreatePanelAsync(
        CreateLabPanelRequest request, ClaimsPrincipal principal, CreateLabPanelHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new CreateLabPanelCommand(
                ParseOrganizationId(principal), request.Code, request.Name, request.Description,
                request.TestIds ?? []), cancellationToken);
            return Results.Created($"/api/lab-catalog/panels/{result.Id}", result);
        }
        catch (InvalidOperationException ex) { return Conflict(ex); }
        catch (ArgumentException ex) { return BadRequest(ex); }
    }

    private static async Task<IResult> CreatePanelVersionAsync(
        Guid id, CreatePanelVersionRequest request, CreatePanelVersionHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new CreatePanelVersionCommand(id, request.TestIds ?? []), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex); }
    }

    // ------------------------- Rangos de referencia ----------------------

    private static async Task<IResult> ListRangesAsync(
        ClaimsPrincipal principal, ListReferenceRangesHandler handler, [FromQuery] Guid testId, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListReferenceRangesQuery(ParseOrganizationId(principal), testId), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateRangeAsync(
        CreateReferenceRangeRequest request, ClaimsPrincipal principal, CreateReferenceRangeHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new CreateReferenceRangeCommand(
                ParseOrganizationId(principal), request.TestId, request.Sex, request.AgeMinYears, request.AgeMaxYears,
                request.Pregnancy, request.Method, request.Low, request.High, request.CriticalLow, request.CriticalHigh,
                request.Unit), cancellationToken);
            return Results.Created($"/api/lab-catalog/reference-ranges/{result.Id}", result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex) { return BadRequest(ex); }
    }

    private static async Task<IResult> UpdateRangeAsync(
        Guid id, UpdateReferenceRangeRequest request, UpdateReferenceRangeHandler handler, CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new UpdateReferenceRangeCommand(
                id, request.Low, request.High, request.CriticalLow, request.CriticalHigh), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    // ------------------------- Ofertas por sede --------------------------

    private static async Task<IResult> SetOfferingAsync(
        SetOfferingRequest request, ClaimsPrincipal principal, SetOfferingHandler handler, CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización",
                detail: "La oferta de pruebas es por sede; el JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        try
        {
            await handler.HandleAsync(new SetOfferingCommand(orgId.Value, request.TestId, request.Offered), cancellationToken);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    // --------------------- Fórmulas calculadas / TAT ---------------------

    private static async Task<IResult> ListDerivedAsync(ClaimsPrincipal principal, ListDerivedCalculationsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListDerivedCalculationsQuery(ParseOrganizationId(principal)), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> ListTatAsync(ClaimsPrincipal principal, ListTatTargetsHandler handler, CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new ListTatTargetsQuery(ParseOrganizationId(principal)), cancellationToken);
        return Results.Ok(result);
    }

    // ------------------------------ helpers ------------------------------

    private static IResult Conflict(Exception ex) =>
        Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);

    private static IResult BadRequest(Exception ex) =>
        Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

// ------------------------------- contracts -------------------------------

public sealed record CreateLabTestRequest(
    string Code, string Name, string Datatype, string? LoincCode, string? Unit,
    string Department, int? FastingHours, string? CollectionType, string? PatientInstruction);

public sealed record UpdateLabTestRequest(
    string Name, string Datatype, string? LoincCode, string? Unit,
    string Department, int? FastingHours, string? CollectionType, string? PatientInstruction);

public sealed record SetActiveRequest(bool Active);

public sealed record CreateLabPanelRequest(string Code, string Name, string? Description, IReadOnlyList<Guid>? TestIds);

public sealed record CreatePanelVersionRequest(IReadOnlyList<Guid>? TestIds);

public sealed record CreateReferenceRangeRequest(
    Guid TestId, string Sex, int? AgeMinYears, int? AgeMaxYears, bool? Pregnancy, string? Method,
    decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh, string? Unit);

public sealed record UpdateReferenceRangeRequest(decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh);

public sealed record SetOfferingRequest(Guid TestId, bool Offered);
