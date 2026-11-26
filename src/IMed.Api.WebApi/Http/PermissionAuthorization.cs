using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;

namespace IMed.Api.WebApi.Http;

/// <summary>
/// Autorización por permiso de la matriz (ADR-007), en reemplazo de los <c>RequireRole</c>
/// con roles quemados en <c>Program.cs</c>.
///
/// <para>Las políticas se llaman <c>perm:&lt;código&gt;</c> y se materializan bajo demanda: sin
/// esto habría que declarar cada permiso en el arranque, y agregar uno significaría tocar
/// <c>Program.cs</c> — exactamente la centralización que la matriz viene a eliminar.</para>
/// </summary>
public sealed class PermissionRequirement(string permissionCode) : IAuthorizationRequirement
{
    /// <summary>Código tal como vino en la política; con <c>|</c> son varios.</summary>
    public string PermissionCode { get; } = permissionCode;

    /// <summary>Basta con tener cualquiera de estos permisos.</summary>
    public IReadOnlyList<string> AnyOf { get; } =
        permissionCode.Split(PermissionPolicy.AnySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Prefijo que distingue una política de permiso de las demás.</summary>
public static class PermissionPolicy
{
    public const string Prefix = "perm:";
    public const char AnySeparator = '|';

    /// <summary>Nombre de política para un permiso. Uso: <c>.RequireAuthorization(PermissionPolicy.For(...))</c>.</summary>
    public static string For(string permissionCode) => Prefix + permissionCode;

    /// <summary>
    /// Política que se cumple con <b>cualquiera</b> de los permisos. Para una acción que hacen dos
    /// funciones distintas —capturar resultados: el técnico (<c>lab.operate</c>) y el bioanalista
    /// (<c>lab.validate</c>)— sin darle a una el permiso de la otra.
    /// </summary>
    public static string ForAny(params string[] permissionCodes) =>
        Prefix + string.Join(AnySeparator, permissionCodes);
}

/// <summary>
/// Crea la política <c>perm:*</c> la primera vez que un endpoint la pide, y delega el resto en
/// el proveedor por defecto.
/// </summary>
public sealed class PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var existing = await base.GetPolicyAsync(policyName);
        if (existing is not null)
        {
            return existing;
        }

        if (!policyName.StartsWith(PermissionPolicy.Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var code = policyName[PermissionPolicy.Prefix.Length..];
        return new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new PermissionRequirement(code))
            .Build();
    }
}

/// <summary>
/// Evalúa el permiso contra la matriz de la organización del token.
///
/// <para>La resolución es por request y no viaja en el JWT: un permiso en el token sería una
/// decisión de autorización cacheada por la vida del token, y revocar dejaría de funcionar hasta
/// que expire.</para>
/// </summary>
public sealed class PermissionAuthorizationHandler(IPermissionResolver resolver)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var orgRaw = context.User.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var organizationId))
        {
            // Sin organización no hay matriz contra la cual evaluar. No se marca fallo explícito:
            // dejar el requisito sin cumplir ya produce 403, y un Fail() cortaría otras políticas
            // que pudieran estar evaluándose sobre el mismo request.
            return;
        }

        var roles = context.User.FindAll("roles").Select(c => c.Value).ToList();
        if (roles.Count == 0)
        {
            return;
        }

        var permissions = await resolver.ResolveAsync(organizationId, roles);
        if (requirement.AnyOf.Any(permissions.Has))
        {
            context.Succeed(requirement);
        }
    }
}
