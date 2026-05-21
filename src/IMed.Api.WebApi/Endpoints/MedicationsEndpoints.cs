using IMed.Api.Application.Medications;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoint del catálogo de medicamentos (Épica K1). El frontend lo consume
/// en la sección "Recetas" del <c>EncounterActivePage</c> para typeahead
/// asistido al armar el <c>MedicationRequest</c>. Texto libre también es
/// posible si el medicamento no está en el slice (el form lo emite igual
/// como <c>medication.text</c>).
/// </summary>
public static class MedicationsEndpoints
{
    public static IEndpointRouteBuilder MapMedicationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/medications/search", SearchAsync)
            .WithTags("Medications")
            .WithName("SearchMedications")
            .WithSummary("Búsqueda typeahead del catálogo WHO ATC de medicamentos.")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SearchAsync(
        [FromQuery] string q,
        SearchMedicationsHandler handler,
        [FromQuery] int? limit,
        [FromQuery] string? category,
        CancellationToken cancellationToken)
    {
        var result = await handler.HandleAsync(
            new SearchMedicationsQuery(q ?? string.Empty, limit ?? 20, category),
            cancellationToken);
        return Results.Ok(result);
    }
}
