using IMed.Api.Application.Abstractions;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Catálogos de campos de los formularios del studio. Solo lectura: los define la plataforma y el
/// cliente los usa para armar el editor y compilar los documentos.
/// </summary>
public static class FormCatalogEndpoints
{
    public static IEndpointRouteBuilder MapFormCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/form-catalog")
            .WithTags("Forms")
            .RequireAuthorization();

        group.MapGet("/", async (IFieldCatalogProvider catalogs, CancellationToken cancellationToken) =>
                Results.Ok(await catalogs.ListAsync(cancellationToken)))
            .WithName("ListFormCatalogs")
            .WithSummary("Tipos de formulario del studio que tienen catálogo.");

        group.MapGet("/{code}", async (string code, IFieldCatalogProvider catalogs, CancellationToken cancellationToken) =>
            {
                var json = await catalogs.GetCatalogJsonAsync(code, cancellationToken);
                return json is null ? Results.NotFound() : Results.Content(json, "application/json");
            })
            .WithName("GetFormCatalog")
            .WithSummary("Catálogo de campos de un tipo de formulario del studio (solo lectura).");

        return app;
    }
}
