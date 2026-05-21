using System.Security.Claims;
using IMed.Api.Application.LabOrders;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints relacionados con órdenes de laboratorio (Épica L5).
/// <list type="bullet">
///   <item><c>GET /api/lab-orders/{id}/pdf</c> — descarga la orden como PDF.</item>
/// </list>
/// La creación/edición sigue hablando con HAPI desde el frontend (vertical
/// slice del piloto).
/// </summary>
public static class LabOrdersEndpoints
{
    public static IEndpointRouteBuilder MapLabOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lab-orders")
            .WithTags("LabOrders")
            .RequireAuthorization();

        group.MapGet("/{id}/pdf", DownloadPdfAsync)
            .WithName("DownloadLabOrderPdf")
            .WithSummary("Devuelve el PDF renderizado de un ServiceRequest (orden de laboratorio).");

        return app;
    }

    private static async Task<IResult> DownloadPdfAsync(
        string id,
        ClaimsPrincipal principal,
        GetLabOrderPdfHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Results.Problem(
                title: "id requerido",
                detail: "Falta el id del ServiceRequest.",
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
            new GetLabOrderPdfQuery(orgId, id), cancellationToken);
        if (result is null)
        {
            return Results.NotFound();
        }

        return Results.File(result.Bytes, "application/pdf", fileDownloadName: result.FileName);
    }
}
