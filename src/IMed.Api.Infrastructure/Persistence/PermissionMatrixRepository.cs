using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Authorization;
using IMed.Api.Domain.Authorization;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Implementación EF Core de la administración de la matriz (ADR-007).</summary>
public sealed class PermissionMatrixRepository(AppDbContext db) : IPermissionMatrixRepository
{
    public async Task<IReadOnlyList<(string Code, string Module, string Description)>> ListPermissionsAsync(
        CancellationToken ct = default)
    {
        var rows = await db.Permissions.AsNoTracking()
            .Select(p => new { p.Code, p.Module, p.Description })
            .ToListAsync(ct);
        return rows.Select(r => (r.Code, r.Module, r.Description)).ToList();
    }

    public async Task<IReadOnlyList<(Guid? OrganizationId, string RoleCode, string PermissionCode, bool Denied)>>
        ListGrantsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var rows = await db.RolePermissions.AsNoTracking()
            .Where(x => x.OrganizationId == null || x.OrganizationId == organizationId)
            .Select(x => new { x.OrganizationId, x.RoleCode, x.PermissionCode, x.Denied })
            .ToListAsync(ct);
        return rows.Select(r => (r.OrganizationId, r.RoleCode, r.PermissionCode, r.Denied)).ToList();
    }

    public async Task ApplyGrantsAsync(
        Guid organizationId,
        string roleCode,
        IReadOnlyList<(string PermissionCode, GrantAction Action)> changes,
        TimeProvider timeProvider,
        CancellationToken ct = default)
    {
        var codes = changes.Select(c => c.PermissionCode).ToList();
        var existing = await db.RolePermissions
            .Where(x => x.OrganizationId == organizationId
                        && x.RoleCode == roleCode
                        && codes.Contains(x.PermissionCode))
            .ToListAsync(ct);

        foreach (var (permissionCode, action) in changes)
        {
            var current = existing.FirstOrDefault(x => x.PermissionCode == permissionCode);
            if (current is not null)
            {
                // Volver a la plantilla es borrar la variante, no escribir el valor que la
                // plantilla tiene hoy: si mañana el producto cambia esa regla, la organización la sigue.
                db.RolePermissions.Remove(current);
            }
            if (action == GrantAction.Inherit)
            {
                continue;
            }
            db.RolePermissions.Add(RolePermission.Create(
                roleCode, permissionCode, PermissionScope.Organization,
                organizationId: organizationId, denied: action == GrantAction.Deny,
                timeProvider: timeProvider));
        }

        // Un solo SaveChanges: EF lo envuelve en una transacción, así que se aplican todos o ninguno.
        await db.SaveChangesAsync(ct);
    }

    public async Task ResetRoleAsync(Guid organizationId, string roleCode, CancellationToken ct = default)
    {
        await db.RolePermissions
            .Where(x => x.OrganizationId == organizationId && x.RoleCode == roleCode)
            .ExecuteDeleteAsync(ct);
    }
}
