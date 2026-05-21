using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Team;
using IMed.Api.Domain.Invitations;

namespace IMed.Api.WebApi.Endpoints;

public static class TeamEndpoints
{
    public static IEndpointRouteBuilder MapTeamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/team", ListTeamAsync)
            .WithTags("Team")
            .WithName("ListTeam")
            .WithSummary("Lista los miembros del equipo de la organización del usuario autenticado.")
            .RequireAuthorization();

        app.MapPut("/api/team/{keycloakUserId:guid}/role", ChangeMemberRoleAsync)
            .WithTags("Team")
            .WithName("ChangeMemberRole")
            .WithSummary("Cambia el rol realm del miembro indicado. Requiere rol org_admin.")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> ListTeamAsync(
        ClaimsPrincipal principal,
        ListTeamHandler handler,
        CancellationToken cancellationToken)
    {
        var organizationId = ParseOrganizationId(principal);
        if (organizationId is null)
        {
            return Results.Problem(
                title: "Sin organización",
                detail: "El JWT no contiene `organization_id`. Completa el bootstrap antes de listar el equipo.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var team = await handler.HandleAsync(new ListTeamQuery(organizationId.Value), cancellationToken);
        return Results.Ok(team);
    }

    private static async Task<IResult> ChangeMemberRoleAsync(
        Guid keycloakUserId,
        ChangeMemberRoleRequest request,
        ClaimsPrincipal principal,
        ChangeMemberRoleHandler handler,
        CancellationToken cancellationToken)
    {
        var (actorId, orgId, error) = ExtractAdminIds(principal);
        if (error is not null)
        {
            return error;
        }

        InvitationRole role;
        try
        {
            role = InvitationRoleNames.FromString(request.Role);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await handler.HandleAsync(new ChangeMemberRoleCommand(
                OrganizationId: orgId!.Value,
                TargetKeycloakUserId: keycloakUserId,
                ActorKeycloakUserId: actorId!.Value,
                NewRole: role), cancellationToken);
            return Results.Ok(new ChangeMemberRoleResponse(
                TargetKeycloakUserId: result.TargetKeycloakUserId,
                OldRole: result.OldRole,
                NewRole: result.NewRole,
                PendingKeycloakSync: result.PendingKeycloakSync));
        }
        catch (ChangeMemberRoleException ex) when (ex.Code == "not_found")
        {
            return Results.Problem(title: "Miembro no encontrado", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (ChangeMemberRoleException ex)
        {
            return Results.Problem(
                title: "No se puede cambiar el rol",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }

    private static (Guid? actorId, Guid? orgId, IResult? error) ExtractAdminIds(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var actorId))
        {
            return (null, null, Results.Problem(
                title: "Token inválido",
                detail: "El JWT no contiene un claim `sub` parseable como UUID.",
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var orgRaw = principal.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var orgId))
        {
            return (null, null, Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (!HasRealmRole(principal, InvitationRoleNames.OrgAdmin))
        {
            return (null, null, Results.Problem(
                title: "Sin permisos",
                detail: $"Esta operación requiere el rol '{InvitationRoleNames.OrgAdmin}'.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (actorId, orgId, null);
    }

    private static bool HasRealmRole(ClaimsPrincipal principal, string roleName)
    {
        var realmAccess = principal.FindFirstValue("realm_access");
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out var rolesEl)
                && rolesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rolesEl.EnumerateArray())
                {
                    if (string.Equals(r.GetString(), roleName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue("organization_id");
        return string.IsNullOrWhiteSpace(raw) ? null : Guid.TryParse(raw, out var id) ? id : null;
    }
}

public sealed record ChangeMemberRoleRequest(string Role);

public sealed record ChangeMemberRoleResponse(
    Guid TargetKeycloakUserId,
    string OldRole,
    string NewRole,
    bool PendingKeycloakSync);
