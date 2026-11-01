using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Forms;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class FormsEndpoints
{
    public static IEndpointRouteBuilder MapFormsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/forms")
            .WithTags("Forms")
            .RequireAuthorization();

        // -----------------------------------------------------------------
        // Lectura — disponible para cualquier user autenticado de la org.
        // -----------------------------------------------------------------
        group.MapGet("/", ListAsync)
            .WithName("ListForms")
            .WithSummary("Lista las definiciones de formularios visibles para la org del usuario.");

        group.MapGet("/{id:guid}", GetByIdAsync)
            .WithName("GetFormById")
            .WithSummary("Obtiene una FormDefinition por ID.");

        group.MapGet("/by-specialty/{code}", GetBySpecialtyAsync)
            .WithName("GetFormBySpecialty")
            .WithSummary("Resuelve la mejor FormDefinition publicada para una especialidad.");

        group.MapGet("/resolve/{code}", ResolveAsync)
            .WithName("ResolveFormByCode")
            .WithSummary("Resuelve la versión publicada más reciente de un formulario por code: org → preset global.");

        group.MapGet("/by-code/{code}", GetByCodeAsync)
            .WithName("GetFormByCode")
            .WithSummary("Resuelve una FormDefinition exacta por code+version (incluye retired). Usada por el detail page del encuentro.");

        // -----------------------------------------------------------------
        // Escritura — solo super-admin / org-admin. La UI super-usuario los consume.
        // En Hito 1 dejamos auth básica (rol exigido); el RBAC fino lo cierra Hito 2.
        // -----------------------------------------------------------------
        group.MapPost("/", CreateAsync)
            .WithName("CreateForm")
            .WithSummary("Crea una FormDefinition en estado draft.");

        group.MapPut("/{id:guid}", UpdateAsync)
            .WithName("UpdateForm")
            .WithSummary("Edita una FormDefinition (solo si está en draft).");

        group.MapPost("/{id:guid}/publish", PublishAsync)
            .WithName("PublishForm")
            .WithSummary("Transita una FormDefinition de draft a published.");

        group.MapPost("/{id:guid}/retire", RetireAsync)
            .WithName("RetireForm")
            .WithSummary("Retira una FormDefinition publicada.");

        group.MapDelete("/{id:guid}", DeleteAsync)
            .WithName("DeleteForm")
            .WithSummary("Borra una FormDefinition (solo en draft).");

        return app;
    }

    // -------------------------- handlers ---------------------------------

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ListFormsHandler handler,
        [FromQuery] bool includeNonPublished,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        var result = await handler.HandleAsync(new ListFormsQuery(orgId, includeNonPublished), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetByIdAsync(
        Guid id,
        GetFormByIdHandler handler,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(new GetFormByIdQuery(id), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetBySpecialtyAsync(
        string code,
        ClaimsPrincipal principal,
        GetFormBySpecialtyHandler handler,
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
        var result = await handler.HandleAsync(new GetFormBySpecialtyQuery(orgId.Value, code), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ResolveAsync(
        string code,
        ClaimsPrincipal principal,
        ResolveFormByCodeHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new ResolveFormByCodeQuery(orgId.Value, code), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> GetByCodeAsync(
        string code,
        [FromQuery] string version,
        ClaimsPrincipal principal,
        GetFormByCodeHandler handler,
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
        var result = await handler.HandleAsync(new GetFormByCodeQuery(orgId.Value, code, version), cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateFormRequest request,
        ClaimsPrincipal principal,
        CreateFormHandler handler,
        CancellationToken cancellationToken)
    {
        var sub = ParseSub(principal);
        if (sub is null)
        {
            return Unauthorized();
        }

        var orgId = ParseOrganizationId(principal);
        // Si el caller pasó un organization_id explícito (super-admin asignando preset global = null),
        // y NO matchea con su org, en Hito 1 lo respetamos pero el RBAC fino se cierra Hito 2.
        var targetOrgId = request.OrganizationIdOverride ?? orgId;

        try
        {
            var result = await handler.HandleAsync(
                new CreateFormCommand(
                    Code: request.Code,
                    Title: request.Title,
                    Description: request.Description,
                    SpecialtyCode: request.SpecialtyCode,
                    Version: request.Version,
                    SchemaJson: request.SchemaJson,
                    FhirMappingJson: request.FhirMappingJson,
                    OrganizationId: targetOrgId,
                    CreatedBy: sub.Value),
                cancellationToken);
            return Results.Created($"/api/forms/{result.Id}", result);
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
        UpdateFormRequest request,
        UpdateFormHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(
                new UpdateFormCommand(
                    Id: id,
                    Title: request.Title,
                    Description: request.Description,
                    SpecialtyCode: request.SpecialtyCode,
                    SchemaJson: request.SchemaJson,
                    FhirMappingJson: request.FhirMappingJson),
                cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> PublishAsync(
        Guid id,
        ClaimsPrincipal principal,
        PublishFormHandler handler,
        CancellationToken cancellationToken)
    {
        var sub = ParseSub(principal);
        if (sub is null)
        {
            return Unauthorized();
        }
        try
        {
            var result = await handler.HandleAsync(new PublishFormCommand(id, sub.Value), cancellationToken);
            return Results.Ok(result);
        }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> RetireAsync(
        Guid id,
        RetireFormHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new RetireFormCommand(id), cancellationToken);
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
        DeleteFormHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            await handler.HandleAsync(new DeleteFormCommand(id), cancellationToken);
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
public sealed record CreateFormRequest(
    string Code,
    string Title,
    string? Description,
    string? SpecialtyCode,
    string Version,
    JsonElement SchemaJson,
    JsonElement FhirMappingJson,
    Guid? OrganizationIdOverride);

public sealed record UpdateFormRequest(
    string Title,
    string? Description,
    string? SpecialtyCode,
    JsonElement SchemaJson,
    JsonElement FhirMappingJson);
