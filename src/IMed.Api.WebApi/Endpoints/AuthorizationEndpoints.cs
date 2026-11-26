using System.Security.Claims;
using IMed.Api.Application.Abstractions;
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
        // Fuera del grupo: cualquier usuario autenticado puede saber qué puede hacer él mismo. El
        // portal lo usa para mostrar u ocultar pantallas y botones según la matriz de su
        // organización, en vez de repetir roles quemados. No es una defensa: cada endpoint vuelve
        // a evaluar su permiso.
        app.MapGet("/api/authorization/me", GetMyPermissionsAsync)
            .WithTags("Authorization")
            .WithName("GetMyPermissions")
            .WithSummary("Permisos efectivos del usuario actual en su organización.")
            .RequireAuthorization();

        var group = app.MapGroup("/api/authorization")
            .WithTags("Authorization")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgPolicyManage));

        group.MapGet("/matrix", GetMatrixAsync)
            .WithName("GetPermissionMatrix")
            .WithSummary("Matriz de roles y permisos de la organización, con el origen de cada concesión.");

        group.MapPut("/matrix", SetGrantAsync)
            .WithName("SetPermissionGrant")
            .WithSummary("Concede, quita o devuelve a la plantilla un permiso de un rol.");

        group.MapPut("/roles/{role}/permissions", SetRolePermissionsAsync)
            .WithName("SetRolePermissions")
            .WithSummary("Fija el estado deseado de varios permisos de un rol, de forma atómica (ADR-009).");

        group.MapDelete("/roles/{role}/overrides", ResetRoleAsync)
            .WithName("ResetRole")
            .WithSummary("Devuelve un rol a los valores de IMed en esta organización (ADR-009).");

        return app;
    }

    private static async Task<IResult> GetMyPermissionsAsync(
        ClaimsPrincipal principal, IPermissionResolver resolver, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var roles = principal.FindAll("roles").Select(c => c.Value).ToList();

        // Sin organización o sin roles no hay matriz que aplicar: lista vacía, no error. Es el
        // estado normal de quien todavía no eligió organización.
        if (orgId is null || roles.Count == 0)
        {
            return Results.Ok(new MyPermissionsResponse([]));
        }

        var effective = await resolver.ResolveAsync(orgId.Value, roles, ct);
        return Results.Ok(new MyPermissionsResponse(effective.Codes.Order(StringComparer.Ordinal).ToList()));
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

    private static async Task<IResult> SetRolePermissionsAsync(
        string role, SetRolePermissionsRequest request, ClaimsPrincipal principal,
        SetRolePermissionsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            await handler.HandleAsync(
                new SetRolePermissionsCommand(orgId.Value, role, request.Changes ?? [], ParseSub(principal)),
                ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Cambio no permitido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ResetRoleAsync(
        string role, ClaimsPrincipal principal, ResetRoleHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            await handler.HandleAsync(new ResetRoleCommand(orgId.Value, role, ParseSub(principal)), ct);
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

public sealed record MyPermissionsResponse(IReadOnlyList<string> Permissions);

public sealed record SetGrantRequest(string RoleCode, string PermissionCode, GrantAction Action);

public sealed record SetRolePermissionsRequest(IReadOnlyList<RolePermissionChange>? Changes);
