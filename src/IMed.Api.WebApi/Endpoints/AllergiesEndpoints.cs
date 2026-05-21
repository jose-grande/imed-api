using IMed.Api.Application.Allergies;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoint del catálogo de alergenos (Épica J1). El frontend lo consume desde
/// el form de preclínica para typeahead asistido; el rol Auxiliar también
/// puede tipear texto libre que no esté en el catálogo (el form lo emite
/// igual al persistir el <c>AllergyIntolerance</c> con coding.text).
/// </summary>
public static class AllergiesEndpoints
{
    public static IEndpointRouteBuilder MapAllergiesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/allergies/search", SearchAsync)
            .WithTags("Allergies")
            .WithName("SearchAllergies")
            .WithSummary("Búsqueda typeahead del slice SNOMED de alergenos comunes.")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string q,
        SearchAllergiesHandler handler,
        [FromQuery] int? limit,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchAllergiesQuery(q ?? string.Empty, limit ?? 20, category),
            cancellationToken);
        return Results.Ok(result);
    }
}
