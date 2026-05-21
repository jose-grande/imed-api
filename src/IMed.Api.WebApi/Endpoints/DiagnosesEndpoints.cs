using IMed.Api.Application.Diagnoses;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class DiagnosesEndpoints
{
    public static IEndpointRouteBuilder MapDiagnosesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/diagnoses/search", SearchAsync)
            .WithTags("Diagnoses")
            .WithName("SearchDiagnoses")
            .WithSummary("Búsqueda typeahead de diagnósticos (ICD-11) vía el servicio de terminología.")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string q,
        SearchDiagnosesHandler handler,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchDiagnosesQuery(q ?? string.Empty, limit ?? 20),
            cancellationToken);
        return Results.Ok(result);
    }
}
