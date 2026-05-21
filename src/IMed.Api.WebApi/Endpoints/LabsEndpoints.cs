using IMed.Api.Application.Labs;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class LabsEndpoints
{
    public static IEndpointRouteBuilder MapLabsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/labs/search", SearchAsync)
            .WithTags("Labs")
            .WithName("SearchLabs")
            .WithSummary("Búsqueda typeahead de estudios de laboratorio (LOINC) vía el servicio de terminología.")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string q,
        SearchLabsHandler handler,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchLabsQuery(q ?? string.Empty, limit ?? 20),
            cancellationToken);
        return Results.Ok(result);
    }
}
