using IMed.Api.Application.Authorization;

namespace IMed.Api.Application.Abstractions;

/// <summary>Lectura del catálogo de permisos y de las concesiones, para administrar la matriz.</summary>
public interface IPermissionMatrixRepository
{
    Task<IReadOnlyList<(string Code, string Module, string Description)>> ListPermissionsAsync(
        CancellationToken ct = default);

    /// <summary>Concesiones que aplican a la organización: las globales más las suyas.</summary>
    Task<IReadOnlyList<(Guid? OrganizationId, string RoleCode, string PermissionCode, bool Denied)>> ListGrantsAsync(
        Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Aplica varios cambios a un rol de la organización en una sola transacción. No toca nunca
    /// la plantilla global.
    /// </summary>
    Task ApplyGrantsAsync(
        Guid organizationId,
        string roleCode,
        IReadOnlyList<(string PermissionCode, GrantAction Action)> changes,
        TimeProvider timeProvider,
        CancellationToken ct = default);

    /// <summary>Borra todas las variantes de un rol en la organización.</summary>
    Task ResetRoleAsync(Guid organizationId, string roleCode, CancellationToken ct = default);
}
