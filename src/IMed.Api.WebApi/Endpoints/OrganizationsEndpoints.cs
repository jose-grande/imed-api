using System.Security.Claims;
using IMed.Api.Application.Organizations;

namespace IMed.Api.WebApi.Endpoints;

public static class OrganizationsEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/organizations")
            .WithTags("Organizations")
            .RequireAuthorization();

        group.MapPost("/bootstrap", BootstrapAsync)
            .WithName("BootstrapOrganization")
            .WithSummary("Crea la primera Organization del usuario recién registrado.");

        return app;
    }

    private static async Task<IResult> BootstrapAsync(
        BootstrapOrganizationRequest request,
        ClaimsPrincipal user,
        BootstrapOrganizationHandler handler,
        CancellationToken cancellationToken)
    {
        // `sub` del JWT = id del usuario en Keycloak. Es un UUID.
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var keycloakUserId))
        {
            return Results.Problem(
                title: "Token inválido",
                detail: "El JWT no contiene un claim `sub` parseable como UUID.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Regla de negocio: solo usuarios SIN organization_id pueden hacer bootstrap.
        // Si ya tienen uno asignado, están intentando cruzar tenants — rechazar.
        var currentOrgId = user.FindFirstValue("organization_id");
        if (!string.IsNullOrWhiteSpace(currentOrgId))
        {
            return Results.Problem(
                title: "Usuario ya pertenece a una Organization",
                detail: $"El usuario ya tiene organization_id='{currentOrgId}'. El bootstrap solo aplica a usuarios recién registrados.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Problem(
                title: "Nombre obligatorio",
                detail: "El campo `name` es obligatorio.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(
            new BootstrapOrganizationCommand(
                KeycloakUserId: keycloakUserId,
                OrganizationName: request.Name,
                NationalTaxId: request.NationalTaxId),
            cancellationToken);

        var message = result.PendingKeycloakSync
            ? "Organization creada. La sincronización con Keycloak quedó pendiente; el sistema reintentará en background. Cuando recibas la notificación de refresh, cerrá sesión y volvé a loguearte."
            : "Organization creada. Cerrá sesión y volvé a loguearte para que el nuevo JWT traiga el organization_id.";

        return Results.Ok(new BootstrapOrganizationResponse(
            OrganizationId: result.OrganizationId,
            OrganizationFhirId: result.OrganizationFhirId,
            AssignedTier: result.AssignedTier.ToString().ToUpperInvariant(),
            PendingKeycloakSync: result.PendingKeycloakSync,
            Message: message));
    }
}

public sealed record BootstrapOrganizationRequest(string Name, string? NationalTaxId);

public sealed record BootstrapOrganizationResponse(
    Guid OrganizationId,
    string OrganizationFhirId,
    string AssignedTier,
    bool PendingKeycloakSync,
    string Message);
