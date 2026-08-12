using System.Text.Json.Serialization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.Authorization;

/// <summary>
/// De dónde sale la concesión de un (rol, permiso) para esta organización.
///
/// <para>Viaja como texto: una matriz de permisos que se lea con números en la red es una que
/// nadie puede depurar mirando la respuesta, y un cambio de orden del enum la rompería en silencio.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GrantOrigin>))]
public enum GrantOrigin
{
    /// <summary>Nadie se lo concede.</summary>
    None = 0,

    /// <summary>Lo concede la plantilla del producto.</summary>
    Template = 1,

    /// <summary>La organización lo concedió explícitamente, además de la plantilla.</summary>
    Granted = 2,

    /// <summary>La organización lo quitó, aunque la plantilla lo conceda.</summary>
    Denied = 3,
}

public sealed record PermissionRowDto(
    string Code,
    string Module,
    string Description,
    IReadOnlyDictionary<string, GrantOrigin> ByRole);

public sealed record PermissionMatrixDto(
    IReadOnlyList<string> Roles,
    IReadOnlyList<PermissionRowDto> Permissions);

/// <summary>
/// Matriz completa para la pantalla de administración (ADR-007).
///
/// <para>Muestra las tres cosas juntas —lo que da la plantilla, lo que la organización agregó y
/// lo que quitó— porque un administrador que solo viera el resultado efectivo no podría saber si
/// un permiso falta porque nadie lo configuró o porque alguien lo quitó a propósito.</para>
/// </summary>
public sealed class GetPermissionMatrixHandler(IPermissionMatrixRepository repo)
{
    public async Task<PermissionMatrixDto> HandleAsync(Guid organizationId, CancellationToken ct = default)
    {
        var catalog = await repo.ListPermissionsAsync(ct);
        var grants = await repo.ListGrantsAsync(organizationId, ct);

        var byKey = grants.ToLookup(g => g.PermissionCode, StringComparer.Ordinal);

        var rows = catalog
            .OrderBy(p => p.Module, StringComparer.Ordinal)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .Select(p =>
            {
                var byRole = new Dictionary<string, GrantOrigin>(StringComparer.Ordinal);
                foreach (var role in PermissionCatalog.Roles)
                {
                    var forRole = byKey[p.Code].Where(g => g.RoleCode == role).ToList();
                    var ownIndex = forRole.FindIndex(g => g.OrganizationId == organizationId);
                    var hasTemplate = forRole.Exists(g => g.OrganizationId is null);

                    byRole[role] = ownIndex >= 0
                        ? (forRole[ownIndex].Denied ? GrantOrigin.Denied : GrantOrigin.Granted)
                        : (hasTemplate ? GrantOrigin.Template : GrantOrigin.None);
                }
                return new PermissionRowDto(p.Code, p.Module, p.Description, byRole);
            })
            .ToList();

        return new PermissionMatrixDto(PermissionCatalog.Roles, rows);
    }
}

/// <summary>Qué quiere hacer el administrador con una celda de la matriz. Viaja como texto.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<GrantAction>))]
public enum GrantAction
{
    /// <summary>Volver a lo que dice la plantilla del producto.</summary>
    Inherit = 0,

    /// <summary>Conceder en esta organización.</summary>
    Grant = 1,

    /// <summary>Quitar en esta organización, aunque la plantilla lo conceda.</summary>
    Deny = 2,
}

public sealed record SetGrantCommand(
    Guid OrganizationId, string RoleCode, string PermissionCode, GrantAction Action, Guid? ChangedBy);

/// <summary>
/// Cambia una celda de la matriz de una organización (ADR-007).
///
/// <para>Nunca toca la plantilla global: una organización configura lo suyo. <c>Inherit</c> borra
/// la variante y vuelve a lo que dice el producto, que es lo que permite deshacer sin tener que
/// recordar cuál era el valor original.</para>
/// </summary>
public sealed class SetPermissionGrantHandler(
    IPermissionMatrixRepository repo,
    IPermissionResolver resolver,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetGrantCommand cmd, CancellationToken ct = default)
    {
        if (!PermissionCatalog.Roles.Contains(cmd.RoleCode, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Rol desconocido: '{cmd.RoleCode}'.", nameof(cmd));
        }
        if (!PermissionCatalog.All.Any(p => string.Equals(p.Code, cmd.PermissionCode, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Permiso desconocido: '{cmd.PermissionCode}'.", nameof(cmd));
        }

        // Guarda contra el pie en el disparador: si un org_admin se quita a sí mismo la
        // administración de la política, nadie de esa organización puede volver a entrar acá.
        if (cmd.RoleCode == "org_admin"
            && cmd.PermissionCode == PermissionCatalog.OrgPolicyManage
            && cmd.Action == GrantAction.Deny)
        {
            throw new InvalidOperationException(
                "No se le puede quitar a org_admin la administración de la política: " +
                "la organización quedaría sin nadie que pueda volver a cambiarla.");
        }

        await repo.SetGrantAsync(cmd, timeProvider, ct);
        await resolver.InvalidateAsync(cmd.OrganizationId, cmd.ChangedBy, ct);
    }
}
