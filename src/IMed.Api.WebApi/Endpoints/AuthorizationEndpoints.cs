using System.Security.Claims;
using IMed.Api.Application.Authorization;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Administración de la matriz de roles y permisos (ADR-007). Bajo <c>/api/authorization</c>.
///
/// <para>Requiere <c>org.policy.manage</c>: quien puede cambiar la política es, por definición,
/// quien puede darse cualquier otro permiso. Es el endpoint más sensible de la API.</para>
/// </summary>
public static class AuthorizationEndpoints
{
    public static IEndpointRouteBuilder MapAuthorizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/authorization")
            .WithTags("Authorization")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgPolicyManage));

        group.MapGet("/matrix", GetMatrixAsync)
            .WithName("GetPermissionMatrix")
            .WithSummary("Matriz de roles y permisos de la organización, con el origen de cada concesión.");

        group.MapPut("/matrix", SetGrantAsync)
            .WithName("SetPermissionGrant")
            .WithSummary("Concede, quita o devuelve a la plantilla un permiso de un rol.");

        return app;
    }

    private static async Task<IResult> GetMatrixAsync(
        ClaimsPrincipal principal, GetPermissionMatrixHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> SetGrantAsync(
        SetGrantRequest request, ClaimsPrincipal principal,
        SetPermissionGrantHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            await handler.HandleAsync(
                new SetGrantCommand(orgId.Value, request.RoleCode, request.PermissionCode,
                    request.Action, ParseSub(principal)),
                ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Cambio no permitido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult Forbidden() =>
        Results.Problem(title: "Sin organización", detail: "El token no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

public sealed record SetGrantRequest(string RoleCode, string PermissionCode, GrantAction Action);
