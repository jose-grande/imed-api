using System.Security.Claims;
using IMed.Api.Application.Patients;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints del flow de Patients (Épica H2). La creación/edición sigue
/// hablando directo con HAPI desde el frontend (Hito 1 vertical slice). Este
/// grupo expone solo operaciones cross-cutting que necesitan datos del tenant:
/// <list type="bullet">
///   <item><c>GET /api/patients/check-duplicate</c> — dedupe dentro de la partition.</item>
///   <item><c>POST /api/patients/{id}/invite-to-portal</c> — stub Hito 4.</item>
/// </list>
/// </summary>
public static class PatientsEndpoints
{
    public static IEndpointRouteBuilder MapPatientsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/patients")
            .WithTags("Patients")
            .RequireAuthorization();

        group.MapGet("/check-duplicate", CheckDuplicateAsync)
            .WithName("CheckDuplicatePatient")
            .WithSummary("Busca pacientes con el mismo identifier en la org del JWT.");

        group.MapPost("/{id}/invite-to-portal", InviteToPortalAsync)
            .WithName("InvitePatientToPortal")
            .WithSummary("Stub Hito 4: invitación al portal de paciente. Devuelve 501.");

        return app;
    }

    private static async Task<IResult> CheckDuplicateAsync(
        [FromQuery] string? identifierSystem,
        [FromQuery] string? identifierValue,
        ClaimsPrincipal principal,
        CheckDuplicatePatientHandler handler,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifierSystem) || string.IsNullOrWhiteSpace(identifierValue))
        {
            return Results.Problem(
                title: "Parámetros requeridos",
                detail: "Se requieren los query params 'identifierSystem' e 'identifierValue'.",
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

        var result = await handler.HandleAsync(new CheckDuplicatePatientQuery(
            OrganizationId: orgId,
            IdentifierSystem: identifierSystem,
            IdentifierValue: identifierValue), cancellationToken);

        return Results.Ok(result);
    }

    private static IResult InviteToPortalAsync(string id)
    {
        return Results.Problem(
            title: "No implementado",
            detail: "La invitación al portal de paciente está planeada para Hito 4.",
            statusCode: StatusCodes.Status501NotImplemented,
            extensions: new Dictionary<string, object?> { ["milestone"] = "Hito 4" });
    }
}
