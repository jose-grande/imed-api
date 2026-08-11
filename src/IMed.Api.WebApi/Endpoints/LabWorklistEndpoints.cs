using System.Security.Claims;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;
using IMed.Api.Application.LabReception;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>Worklist operacional del laboratorio (Épica LIS-3, L3-F3). Política <c>LabRead</c>.</summary>
public static class LabWorklistEndpoints
{
    public static IEndpointRouteBuilder MapLabWorklistEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lab/worklist", GetAsync)
            .WithTags("LabWorklist")
            .WithName("GetLabWorklist")
            .WithSummary("Worklist in-house por estado/prioridad con TAT transcurrido.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.LabRead));
        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal, GetWorklistHandler handler, [FromQuery] string? status, CancellationToken ct)
    {
        var orgId = Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : (Guid?)null;
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        var result = await handler.HandleAsync(new WorklistQuery(orgId.Value, status), ct);
        return Results.Ok(result);
    }
}
