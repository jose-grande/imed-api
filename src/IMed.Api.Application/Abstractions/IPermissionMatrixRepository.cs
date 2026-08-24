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

    /// <summary>Aplica el cambio de una celda. No toca nunca la plantilla global.</summary>
    Task SetGrantAsync(SetGrantCommand cmd, TimeProvider timeProvider, CancellationToken ct = default);
}
