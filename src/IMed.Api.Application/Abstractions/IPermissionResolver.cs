using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.Abstractions;

/// <summary>Permisos efectivos de un conjunto de roles dentro de una organización.</summary>
public sealed record EffectivePermissions(
    IReadOnlySet<string> Codes,
    IReadOnlyDictionary<string, PermissionScope> Scopes)
{
    public bool Has(string permissionCode) => Codes.Contains(permissionCode);

    /// <summary>Alcance más amplio concedido para el permiso, o null si no lo tiene.</summary>
    public PermissionScope? ScopeOf(string permissionCode) =>
        Scopes.TryGetValue(permissionCode, out var scope) ? scope : null;

    public static EffectivePermissions Empty { get; } =
        new(new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, PermissionScope>(StringComparer.Ordinal));
}

/// <summary>
/// Resuelve qué puede hacer un usuario a partir de sus roles y de la matriz (ADR-007).
///
/// <para><b>Por request, no en el token.</b> Los roles viajan en el JWT y está bien; los permisos
/// se resuelven acá. Meterlos en el token sería cachear una decisión de autorización por la vida
/// del token, y entonces revocar dejaría de funcionar hasta que expire.</para>
/// </summary>
public interface IPermissionResolver
{
    Task<EffectivePermissions> ResolveAsync(
        Guid organizationId, IEnumerable<string> roles, CancellationToken ct = default);

    /// <summary>Invalida la caché de una organización tras un cambio de su matriz.</summary>
    Task InvalidateAsync(Guid organizationId, Guid? changedBy, CancellationToken ct = default);
}
