using System.Security.Claims;
using IMed.Api.Application.Profile;

namespace IMed.Api.WebApi.Endpoints;

public static class MeEndpoints
{
    public static IEndpointRouteBuilder MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me")
            .WithTags("Me")
            .RequireAuthorization();

        group.MapGet("/", GetMyProfileAsync)
            .WithName("GetMyProfile")
            .WithSummary("Devuelve el perfil consolidado del usuario autenticado.");

        return app;
    }

    private static async Task<IResult> GetMyProfileAsync(
        ClaimsPrincipal principal,
        GetMyProfileHandler handler,
        CancellationToken cancellationToken)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var keycloakUserId))
        {
            return Results.Problem(
                title: "Token inválido",
                detail: "El JWT no contiene un claim `sub` parseable como UUID.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        var organizationId = ParseOrganizationId(principal);
        var roles = ExtractRoles(principal);

        var user = new UserProfile(
            KeycloakUserId: keycloakUserId,
            Name: principal.FindFirstValue("name") ?? principal.FindFirstValue("preferred_username"),
            Email: principal.FindFirstValue("email") ?? principal.FindFirstValue(ClaimTypes.Email),
            Roles: roles);

        var response = await handler.HandleAsync(
            new GetMyProfileQuery(keycloakUserId, organizationId),
            user,
            cancellationToken);

        return Results.Ok(response);
    }

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue("organization_id");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static string[] ExtractRoles(ClaimsPrincipal principal)
    {
        // Keycloak emite roles realm-level dentro de `realm_access.roles` (JSON anidado).
        // El mapper por defecto de JwtBearer no los promueve a claims top-level.
        // Acá los extraemos del raw claim si está presente.
        var realmAccessRaw = principal.FindFirstValue("realm_access");
        if (string.IsNullOrWhiteSpace(realmAccessRaw))
        {
            return [];
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(realmAccessRaw);
            if (doc.RootElement.TryGetProperty("roles", out var rolesEl) && rolesEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                return rolesEl.EnumerateArray()
                    .Select(x => x.GetString() ?? string.Empty)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToArray();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // claim malformado — devolver vacío
        }

        return [];
    }
}
