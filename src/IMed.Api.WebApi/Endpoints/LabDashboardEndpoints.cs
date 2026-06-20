using System.Security.Claims;
using IMed.Api.Application.LabDashboard;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>Dashboard operacional del laboratorio (Épica LIS-8). Política <c>LabRead</c>.</summary>
public static class LabDashboardEndpoints
{
    public static IEndpointRouteBuilder MapLabDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/lab/dashboard", GetAsync)
            .WithTags("LabDashboard")
            .WithName("GetLabDashboard")
            .WithSummary("Métricas operacionales: recibidas, en lab, validación, críticos, TAT, prioridad.")
            .RequireAuthorization("LabRead");
        return app;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal principal, GetLabDashboardHandler handler, CancellationToken ct)
    {
        var orgId = Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : (Guid?)null;
        if (orgId is null)
        {
            return Results.Problem(title: "Sin organización", statusCode: StatusCodes.Status403Forbidden);
        }
        return Results.Ok(await handler.HandleAsync(new GetLabDashboardQuery(orgId.Value), ct));
    }
}
