using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Resolución de permisos contra la matriz, con caché local (ADR-007).
///
/// <para><b>Por qué caché local y no distribuida.</b> La matriz completa son unos pocos roles por
/// unas decenas de permisos: kilobytes. <c>IMemoryCache</c> da lookups en microsegundos sin
/// dependencia de red. Redis agregaría un componente operativo, un modo de falla nuevo y una
/// superficie de ataque más, para cachear algo que cabe en un correo. Se vuelve la respuesta
/// correcta cuando aparezca revocación inmediata multi-instancia, permisos dinámicos por usuario o
/// blacklisting de tokens — y esta clase está detrás de una interfaz para que cambiarlo sea un día
/// de trabajo, no una reescritura.</para>
///
/// <para><b>Invalidación, que es lo difícil.</b> El TTL propaga altas; no sirve para una
/// revocación. Antes de servir desde caché se compara <c>policy_version</c> —una lectura de una
/// fila por índice— y si cambió, se recarga. Así degradar a alguien surte efecto en el request
/// siguiente y no cuando venza el TTL.</para>
/// </summary>
public sealed class PermissionResolver(
    AppDbContext db,
    IMemoryCache cache,
    TimeProvider timeProvider) : IPermissionResolver
{
    /// <summary>
    /// Techo del TTL. No es el mecanismo de correctitud —eso lo da <c>policy_version</c>— sino
    /// un límite para que una entrada olvidada no viva para siempre.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<EffectivePermissions> ResolveAsync(
        Guid organizationId, IEnumerable<string> roles, CancellationToken ct = default)
    {
        var roleList = roles?
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList() ?? [];
        if (roleList.Count == 0)
        {
            return EffectivePermissions.Empty;
        }

        var version = await CurrentVersionAsync(organizationId, ct);
        var key = $"perm:{organizationId}:{version}:{string.Join(',', roleList)}";
        if (cache.TryGetValue(key, out EffectivePermissions? cached) && cached is not null)
        {
            return cached;
        }

        var resolved = await LoadAsync(organizationId, roleList, ct);
        cache.Set(key, resolved, CacheTtl);
        return resolved;
    }

    public async Task InvalidateAsync(Guid organizationId, Guid? changedBy, CancellationToken ct = default)
    {
        var row = await db.PolicyVersions.FirstOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        if (row is null)
        {
            // Primera modificación de este tenant: nace en 1 y ya cuenta como cambio respecto de
            // la ausencia de fila, que se resuelve como versión 0.
            db.PolicyVersions.Add(PolicyVersion.Create(organizationId, timeProvider));
        }
        else
        {
            row.Bump(changedBy, timeProvider);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Versión combinada: la global más la del tenant. Un cambio en cualquiera de las dos
    /// cambia la clave de caché, porque la resolución mezcla ambas.
    /// </summary>
    private async Task<string> CurrentVersionAsync(Guid organizationId, CancellationToken ct)
    {
        var rows = await db.PolicyVersions.AsNoTracking()
            .Where(x => x.OrganizationId == PolicyVersion.GlobalScope || x.OrganizationId == organizationId)
            .Select(x => new { x.OrganizationId, x.Version })
            .ToListAsync(ct);

        var global = rows.FirstOrDefault(r => r.OrganizationId == PolicyVersion.GlobalScope)?.Version ?? 0;
        var own = rows.FirstOrDefault(r => r.OrganizationId == organizationId)?.Version ?? 0;
        return $"{global}.{own}";
    }

    private async Task<EffectivePermissions> LoadAsync(
        Guid organizationId, IReadOnlyList<string> roles, CancellationToken ct)
    {
        var rows = await db.RolePermissions.AsNoTracking()
            .Where(x => (x.OrganizationId == null || x.OrganizationId == organizationId)
                        && roles.Contains(x.RoleCode))
            .Select(x => new { x.OrganizationId, x.RoleCode, x.PermissionCode, x.Scope, x.Denied })
            .ToListAsync(ct);

        var codes = new HashSet<string>(StringComparer.Ordinal);
        var scopes = new Dictionary<string, PermissionScope>(StringComparer.Ordinal);

        foreach (var group in rows.GroupBy(r => new { r.RoleCode, r.PermissionCode }))
        {
            // La fila del tenant reemplaza a la global para ese (rol, permiso). Es lo que permite
            // que una clínica le quite a un rol algo que la plantilla le da, sin tocar la plantilla.
            var effective = group.FirstOrDefault(r => r.OrganizationId == organizationId)
                            ?? group.First();
            if (effective.Denied)
            {
                continue;
            }

            codes.Add(effective.PermissionCode);
            // Si dos roles conceden el mismo permiso con alcances distintos, gana el más amplio:
            // los roles se suman, no se restringen entre sí.
            if (!scopes.TryGetValue(effective.PermissionCode, out var current) || effective.Scope > current)
            {
                scopes[effective.PermissionCode] = effective.Scope;
            }
        }

        return new EffectivePermissions(codes, scopes);
    }
}
