using System.Security.Claims;
using IMed.Api.Application.Prescriptions;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints relacionados con recetas (Épica K5).
/// <list type="bullet">
///   <item><c>GET /api/prescriptions/{id}/pdf</c> — descarga la receta como PDF.</item>
/// </list>
/// La creación/edición sigue hablando con HAPI desde el frontend (vertical
/// slice del piloto); este grupo expone solo operaciones que necesitan
/// composición server-side.
/// </summary>
public static class PrescriptionsEndpoints
{
    public static IEndpointRouteBuilder MapPrescriptionsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/prescriptions")
            .WithTags("Prescriptions")
            .RequireAuthorization();

        group.MapGet("/{id}/pdf", DownloadPdfAsync)
            .WithName("DownloadPrescriptionPdf")
            .WithSummary("Devuelve el PDF renderizado de un MedicationRequest.");

        return app;
    }

    private static async Task<IResult> DownloadPdfAsync(
        string id,
        ClaimsPrincipal principal,
        GetPrescriptionPdfHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Problem(
                title: "id requerido",
                detail: "Falta el id del MedicationRequest.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var orgRaw = principal.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var orgId))
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(
            new GetPrescriptionPdfQuery(orgId, id), cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
    }
}
