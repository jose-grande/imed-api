using IMed.Api.Application.Catalogs;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class SpecialtiesEndpoints
{
    public static IEndpointRouteBuilder MapSpecialtiesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/specialties/search", SearchAsync)
            .WithTags("Specialties")
            .WithName("SearchSpecialties")
            .WithSummary("Búsqueda typeahead del catálogo de especialidades médicas (SNOMED CT).")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string q,
        SearchSpecialtyHandler handler,
        [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchSpecialtyQuery(q ?? string.Empty, limit ?? 20),
            cancellationToken);
        return Results.Ok(result);
    }
}
