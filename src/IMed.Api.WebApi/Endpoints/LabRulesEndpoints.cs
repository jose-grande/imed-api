using System.Security.Claims;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;
using IMed.Api.Application.LabRules;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// CRUD de reglas clínicas del laboratorio (Épica LIS-5, F2). Lectura `LabRead`,
/// escritura `LabSupervisor`. La UI no-code (F4) las consume.
/// </summary>
public static class LabRulesEndpoints
{
    public static IEndpointRouteBuilder MapLabRulesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab-catalog/rules").WithTags("LabRules").RequireAuthorization(PermissionPolicy.For(PermissionCatalog.LabRead));

        group.MapGet("/", ListAsync).WithName("ListLabRules");
        group.MapPost("/", CreateAsync).WithName("CreateLabRule").RequireAuthorization(PermissionPolicy.For(PermissionCatalog.LabManage));

        return app;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal principal, ListLabRulesHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ListLabRulesQuery(ParseOrg(principal)), ct);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateAsync(
        CreateLabRuleRequest request, ClaimsPrincipal principal, CreateLabRuleHandler handler, CancellationToken ct)
    {
        try
        {
            var result = await handler.HandleAsync(new CreateLabRuleCommand(
                ParseOrg(principal), request.Code, request.Name, request.Scope, request.TargetLoinc,
                request.Action, request.Severity, request.Message, request.LogicJson), ct);
            return Results.Created($"/api/lab-catalog/rules/{result.Id}", result);
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
}

public sealed record CreateLabRuleRequest(
    string Code, string Name, string Scope, string? TargetLoinc,
    string Action, string Severity, string? Message, string LogicJson);
