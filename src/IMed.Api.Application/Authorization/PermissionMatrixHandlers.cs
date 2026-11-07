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

/// <param name="Roles">Roles que ve la clínica. Nunca incluye <c>imed_superadmin</c> (ADR-009 §1).</param>
/// <param name="Permissions">Una fila por permiso del catálogo, con el origen de cada concesión.</param>
/// <param name="LockedRoles">Roles visibles pero no personalizables: la pantalla los muestra en solo lectura.</param>
public sealed record PermissionMatrixDto(
    IReadOnlyList<string> Roles,
    IReadOnlyList<PermissionRowDto> Permissions,
    IReadOnlyList<string> LockedRoles);

/// <summary>
/// Matriz completa para la pantalla de administración (ADR-007, ADR-009).
///
/// <para>Muestra las tres cosas juntas —lo que da la plantilla, lo que la organización agregó y
/// lo que quitó— porque un administrador que solo viera el resultado efectivo no podría saber si
/// un permiso falta porque nadie lo configuró o porque alguien lo quitó a propósito.</para>
///
/// <para>Para los roles bloqueados se ignoran las variantes de la organización, igual que hace el
/// resolver: la pantalla tiene que mostrar lo que de verdad se aplica.</para>
/// </summary>
public sealed class GetPermissionMatrixHandler(IPermissionMatrixRepository repo)
{
    public async Task<PermissionMatrixDto> HandleAsync(Guid organizationId, CancellationToken ct = default)
    {
        var catalog = await repo.ListPermissionsAsync(ct);
        var grants = await repo.ListGrantsAsync(organizationId, ct);

        var byKey = grants.ToLookup(g => g.PermissionCode, StringComparer.Ordinal);
        var roles = PermissionCatalog.VisibleRoles;

        var rows = catalog
            .OrderBy(p => p.Module, StringComparer.Ordinal)
            .ThenBy(p => p.Code, StringComparer.Ordinal)
            .Select(p =>
            {
                var byRole = new Dictionary<string, GrantOrigin>(StringComparer.Ordinal);
                foreach (var role in roles)
                {
                    byRole[role] = PermissionGrantRules.OriginOf(byKey[p.Code], role, organizationId);
                }
                return new PermissionRowDto(p.Code, p.Module, p.Description, byRole);
            })
            .ToList();

        return new PermissionMatrixDto(
            roles,
            rows,
            PermissionCatalog.LockedRoles.Where(roles.Contains).ToList());
    }
}

/// <summary>Reglas compartidas por la lectura y la escritura de la matriz.</summary>
internal static class PermissionGrantRules
{
    public static GrantOrigin OriginOf(
        IEnumerable<(Guid? OrganizationId, string RoleCode, string PermissionCode, bool Denied)> grantsForPermission,
        string role,
        Guid organizationId)
    {
        var forRole = grantsForPermission.Where(g => g.RoleCode == role).ToList();
        var hasTemplate = forRole.Exists(g => g.OrganizationId is null);
        var own = PermissionCatalog.IsLocked(role)
            ? -1
            : forRole.FindIndex(g => g.OrganizationId == organizationId);

        return own >= 0
            ? (forRole[own].Denied ? GrantOrigin.Denied : GrantOrigin.Granted)
            : (hasTemplate ? GrantOrigin.Template : GrantOrigin.None);
    }

    /// <summary>Valida rol y permiso. Lanza <see cref="InvalidOperationException"/> (409) si el rol está bloqueado.</summary>
    public static void EnsureEditable(string roleCode)
    {
        if (!PermissionCatalog.Roles.Contains(roleCode, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Rol desconocido: '{roleCode}'.", nameof(roleCode));
        }
        if (PermissionCatalog.IsLocked(roleCode))
        {
            throw new InvalidOperationException(
                $"El rol '{roleCode}' no se puede personalizar: es un rol del producto que " +
                "tiene que conservar todos sus permisos para que la organización no quede sin " +
                "nadie que la administre.");
        }
    }

    public static void EnsureKnownPermission(string permissionCode)
    {
        if (!PermissionCatalog.All.Any(p => string.Equals(p.Code, permissionCode, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Permiso desconocido: '{permissionCode}'.", nameof(permissionCode));
        }
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
///
/// <para>Se conserva por compatibilidad; la pantalla usa <see cref="SetRolePermissionsHandler"/>.</para>
/// </summary>
public sealed class SetPermissionGrantHandler(
    IPermissionMatrixRepository repo,
    IPermissionResolver resolver,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetGrantCommand cmd, CancellationToken ct = default)
    {
        PermissionGrantRules.EnsureEditable(cmd.RoleCode);
        PermissionGrantRules.EnsureKnownPermission(cmd.PermissionCode);

        await repo.ApplyGrantsAsync(
            cmd.OrganizationId, cmd.RoleCode, [(cmd.PermissionCode, cmd.Action)], timeProvider, ct);
        await resolver.InvalidateAsync(cmd.OrganizationId, cmd.ChangedBy, ct);
    }
}

/// <summary>Un permiso y el estado que el administrador quiere para él.</summary>
public sealed record RolePermissionChange(string PermissionCode, bool Enabled);

public sealed record SetRolePermissionsCommand(
    Guid OrganizationId, string RoleCode, IReadOnlyList<RolePermissionChange> Changes, Guid? ChangedBy);

/// <summary>
/// Fija el estado de varios permisos de un rol de una vez (ADR-009 §2).
///
/// <para><b>El administrador dice qué quiere, no qué acción aplicar.</b> El servidor compara con la
/// plantilla y guarda lo mínimo: si lo pedido coincide con la plantilla se borra la variante, para
/// que la clínica siga recibiendo las mejoras futuras en lo que no tocó.</para>
///
/// <para><b>Atómico.</b> El selector de nivel de expediente cambia hasta cuatro permisos; aplicar
/// la mitad dejaría al rol en una combinación que nadie eligió.</para>
/// </summary>
public sealed class SetRolePermissionsHandler(
    IPermissionMatrixRepository repo,
    IPermissionResolver resolver,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(SetRolePermissionsCommand cmd, CancellationToken ct = default)
    {
        PermissionGrantRules.EnsureEditable(cmd.RoleCode);
        if (cmd.Changes is null || cmd.Changes.Count == 0)
        {
            throw new ArgumentException("No hay cambios que aplicar.", nameof(cmd));
        }
        foreach (var change in cmd.Changes)
        {
            PermissionGrantRules.EnsureKnownPermission(change.PermissionCode);
        }
        if (cmd.Changes.Select(c => c.PermissionCode).Distinct(StringComparer.Ordinal).Count() != cmd.Changes.Count)
        {
            throw new ArgumentException("Un permiso viene más de una vez en el mismo cambio.", nameof(cmd));
        }

        // La plantilla se lee de la base, que es lo que aplica el resolver, y no de BaseMatrix: si
        // alguna vez divergieran, lo que decide es lo sembrado.
        var template = (await repo.ListGrantsAsync(cmd.OrganizationId, ct))
            .Where(g => g.OrganizationId is null && g.RoleCode == cmd.RoleCode && !g.Denied)
            .Select(g => g.PermissionCode)
            .ToHashSet(StringComparer.Ordinal);

        var actions = cmd.Changes
            .Select(c => (c.PermissionCode, template.Contains(c.PermissionCode) == c.Enabled
                ? GrantAction.Inherit
                : c.Enabled ? GrantAction.Grant : GrantAction.Deny))
            .ToList();

        await repo.ApplyGrantsAsync(cmd.OrganizationId, cmd.RoleCode, actions, timeProvider, ct);
        await resolver.InvalidateAsync(cmd.OrganizationId, cmd.ChangedBy, ct);
    }
}

public sealed record ResetRoleCommand(Guid OrganizationId, string RoleCode, Guid? ChangedBy);

/// <summary>
/// Devuelve un rol a los valores de IMed en esta organización: borra todas sus variantes
/// (ADR-009 §2). No toca la plantilla ni a otras organizaciones.
/// </summary>
public sealed class ResetRoleHandler(
    IPermissionMatrixRepository repo,
    IPermissionResolver resolver)
{
    public async Task HandleAsync(ResetRoleCommand cmd, CancellationToken ct = default)
    {
        PermissionGrantRules.EnsureEditable(cmd.RoleCode);
        await repo.ResetRoleAsync(cmd.OrganizationId, cmd.RoleCode, ct);
        await resolver.InvalidateAsync(cmd.OrganizationId, cmd.ChangedBy, ct);
    }
}
