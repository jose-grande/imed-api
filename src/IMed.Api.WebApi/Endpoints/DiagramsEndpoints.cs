using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Diagrams;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class DiagramsEndpoints
{
    public static IEndpointRouteBuilder MapDiagramsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/diagrams")
            .WithTags("Diagrams")
            .RequireAuthorization();

        // -----------------------------------------------------------------
        // Lectura — disponible para cualquier user autenticado de la org.
        // -----------------------------------------------------------------
        group.MapGet("/", ListAsync)
            .WithName("ListDiagrams")
            .WithSummary("Lista las definiciones de diagramas visibles para la org del usuario.");

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetDiagramById")
            .WithSummary("Obtiene una DiagramDefinition por ID.");

        group.MapGet("/by-specialty/{code}", GetBySpecialtyAsync)
            .WithName("GetDiagramsBySpecialty")
            .WithSummary("Resuelve TODOS los diagramas publicados para una especialidad (a diferencia de forms, puede haber varios).");

        group.MapGet("/by-code/{code}", GetByCodeAsync)
            .WithName("GetDiagramByCode")
            .WithSummary("Resuelve una DiagramDefinition exacta por code+version (incluye retired).");

        // -----------------------------------------------------------------
        // Escritura — solo super-admin / org-admin. Mismo nivel de auth que Forms
        // (Hito 1: rol exigido; RBAC fino pendiente).
        // -----------------------------------------------------------------
        group.MapPost("/", CreateAsync)
            .WithName("CreateDiagram")
            .WithSummary("Crea una DiagramDefinition en estado draft.");

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateDiagram")
            .WithSummary("Edita una DiagramDefinition (solo si está en draft).");

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .WithName("PublishDiagram")
            .WithSummary("Transita una DiagramDefinition de draft a published.");

        group.MapPost("/{id:guid}/retire", RetireAsync)
            .WithName("RetireDiagram")
            .WithSummary("Retira una DiagramDefinition publicada.");

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteDiagram")
            .WithSummary("Borra una DiagramDefinition (solo en draft).");

        return app;
    }

    // -------------------------- handlers ---------------------------------

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ListDiagramsHandler handler,
        [FromQuery] bool includeNonPublished,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        var result = await handler.HandleAsync(new ListDiagramsQuery(orgId, includeNonPublished), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        GetDiagramByIdHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetDiagramByIdQuery(id), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetBySpecialtyAsync(
        string code,
        ClaimsPrincipal principal,
        GetDiagramsBySpecialtyHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id. Bootstrap pendiente.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new GetDiagramsBySpecialtyQuery(orgId.Value, code), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetByCodeAsync(
        string code,
        [FromQuery] string version,
        ClaimsPrincipal principal,
        GetDiagramByCodeHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Results.Problem(
                title: "Falta version",
                detail: "El query param 'version' es requerido.",
                statusCode: StatusCodes.Status400BadRequest);
        }
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new GetDiagramByCodeQuery(orgId.Value, code, version), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateDiagramRequest request,
        ClaimsPrincipal principal,
        CreateDiagramHandler handler,
        CancellationToken cancellationToken)
    {
        var sub = ParseSub(principal);
        if (sub is null)
        {
            return Unauthorized();
        }

        var orgId = ParseOrganizationId(principal);
        var targetOrgId = request.OrganizationIdOverride ?? orgId;

        try
        {
            var result = await handler.HandleAsync(
                new CreateDiagramCommand(
                    Code: request.Code,
                    Title: request.Title,
                    Description: request.Description,
                    SpecialtyCode: request.SpecialtyCode,
                    Version: request.Version,
                    SvgContent: request.SvgContent,
                    ZonesJson: request.ZonesJson,
                    AnnotationStyleJson: request.AnnotationStyleJson,
                    LinkedFormCodesJson: request.LinkedFormCodesJson,
                    ZoneBodySiteMapJson: request.ZoneBodySiteMapJson,
                    OrganizationId: targetOrgId,
                    CreatedBy: sub.Value),
                cancellationToken);
            return Results.Created($"/api/diagrams/{result.Id}", result);
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

    private static async Task<IResult> UpdateAsync(
        Guid id,
        UpdateDiagramRequest request,
        UpdateDiagramHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(
                new UpdateDiagramCommand(
                    Id: id,
                    Title: request.Title,
                    Description: request.Description,
                    SpecialtyCode: request.SpecialtyCode,
                    SvgContent: request.SvgContent,
                    ZonesJson: request.ZonesJson,
                    AnnotationStyleJson: request.AnnotationStyleJson,
                    LinkedFormCodesJson: request.LinkedFormCodesJson,
                    ZoneBodySiteMapJson: request.ZoneBodySiteMapJson),
                cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> PublishAsync(
        Guid id,
        ClaimsPrincipal principal,
        PublishDiagramHandler handler,
        CancellationToken cancellationToken)
    {
        var sub = ParseSub(principal);
        if (sub is null)
        {
            return Unauthorized();
        }
        try
        {
            var result = await handler.HandleAsync(new PublishDiagramCommand(id, sub.Value), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RetireAsync(
        Guid id,
        RetireDiagramHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new RetireDiagramCommand(id), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> DeleteAsync(
        Guid id,
        DeleteDiagramHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler.HandleAsync(new DeleteDiagramCommand(id), cancellationToken);
            return Results.NoContent();
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    // -------------------------- helpers ----------------------------------

    private static IResult Unauthorized() =>
        Results.Problem(title: "Token inválido", statusCode: StatusCodes.Status401Unauthorized);

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue("organization_id");
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}

// -------------------------- contracts -------------------------------------
public sealed record CreateDiagramRequest(
    string Code,
    string Title,
    string? Description,
    string? SpecialtyCode,
    string Version,
    string SvgContent,
    JsonElement ZonesJson,
    JsonElement? AnnotationStyleJson,
    JsonElement? LinkedFormCodesJson,
    JsonElement? ZoneBodySiteMapJson,
    Guid? OrganizationIdOverride);

public sealed record UpdateDiagramRequest(
    string Title,
    string? Description,
    string? SpecialtyCode,
    string SvgContent,
    JsonElement ZonesJson,
    JsonElement? AnnotationStyleJson,
    JsonElement? LinkedFormCodesJson,
    JsonElement? ZoneBodySiteMapJson);
