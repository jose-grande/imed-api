using System.Security.Claims;
using IMed.Api.Application.LabSpecimens;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints de operación de muestra (R5, madurez preanalítica): alícuotas,
/// almacenamiento/descarte y chequeo de estabilidad. Bajo <c>/api/lab/specimens</c>.
/// </summary>
public static class LabSpecimenEndpoints
{
    public static IEndpointRouteBuilder MapLabSpecimenEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab/specimens")
            .WithTags("LabSpecimens")
            .RequireAuthorization("LabRead");

        group.MapPost("/aliquot", AliquotAsync)
            .WithName("CreateAliquot")
            .WithSummary("Divide una muestra recibida en N alícuotas (Specimen.parent).")
            .RequireAuthorization("LabTechnician");

        group.MapPost("/store", StoreAsync)
            .WithName("RecordStorage")
            .WithSummary("Registra almacenamiento o descarte de una muestra.")
            .RequireAuthorization("LabTechnician");

        group.MapGet("/stability/{intakeId:guid}", StabilityAsync)
            .WithName("CheckStability")
            .WithSummary("¿La muestra sigue dentro de su ventana de estabilidad (add-on)?");

        return app;
    }

    private static async Task<IResult> AliquotAsync(
        AliquotRequest request, ClaimsPrincipal principal, CreateAliquotHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            var result = await handler.HandleAsync(orgId.Value, request.IntakeId, request.Count, userId.Value, request.Note, ct);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> StoreAsync(
        StoreRequest request, ClaimsPrincipal principal, RecordStorageHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        try
        {
            await handler.HandleAsync(orgId.Value, request.IntakeId, request.Location, request.Disposal, userId.Value, request.Note, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }

    private static async Task<IResult> StabilityAsync(
        Guid intakeId, ClaimsPrincipal principal, CheckStabilityHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var result = await handler.HandleAsync(orgId.Value, intakeId, ct);
        return result is null ? Results.NotFound() : Results.Ok(result);
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

public sealed record AliquotRequest(Guid IntakeId, int Count, string? Note);

public sealed record StoreRequest(Guid IntakeId, string Location, bool Disposal, string? Note);
