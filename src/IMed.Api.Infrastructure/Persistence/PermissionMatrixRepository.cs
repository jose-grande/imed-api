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

    public async Task SetGrantAsync(SetGrantCommand cmd, TimeProvider timeProvider, CancellationToken ct = default)
    {
        var existing = await db.RolePermissions.FirstOrDefaultAsync(
            x => x.OrganizationId == cmd.OrganizationId
                 && x.RoleCode == cmd.RoleCode
                 && x.PermissionCode == cmd.PermissionCode,
            ct);

        if (cmd.Action == GrantAction.Inherit)
        {
            // Volver a la plantilla es borrar la variante, no escribir el valor que la plantilla
            // tiene hoy: si mañana el producto cambia esa regla, la organización la sigue.
            if (existing is not null)
            {
                db.RolePermissions.Remove(existing);
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        var denied = cmd.Action == GrantAction.Deny;
        if (existing is not null)
        {
            db.RolePermissions.Remove(existing);
        }
        db.RolePermissions.Add(RolePermission.Create(
            cmd.RoleCode, cmd.PermissionCode, PermissionScope.Organization,
            organizationId: cmd.OrganizationId, denied: denied, timeProvider: timeProvider));

        await db.SaveChangesAsync(ct);
    }
}
