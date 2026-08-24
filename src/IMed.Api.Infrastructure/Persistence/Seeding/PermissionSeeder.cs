using IMed.Api.Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Siembra el catálogo de permisos y la matriz base del producto (ADR-007).
///
/// <para>El origen es <see cref="PermissionCatalog"/>, que vive en código y por lo tanto en git:
/// así queda historial de quién cambió qué regla y cuándo. La base es donde la política se
/// consulta y donde cada organización puede sobreescribirla.</para>
///
/// <para><b>Reconcilia, no solo inserta.</b> Un permiso nuevo del catálogo tiene que llegar a las
/// instalaciones existentes; si el seeder se saltara cuando ya hay filas, un permiso agregado hoy
/// no existiría nunca en producción. Solo toca las filas <b>globales</b>: las variantes de cada
/// organización son decisiones del tenant y no se pisan.</para>
/// </summary>
public sealed class PermissionSeeder(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<PermissionSeeder> logger) : IHostedService
{
    /// <summary>
    /// <b>Este seeder no traga excepciones, a diferencia de los demás.</b> Sin matriz no hay
    /// permiso que conceder, así que todos los endpoints devuelven 403: la aplicación arranca y
    /// no sirve para nada. Fallar cerrado es lo correcto; fallar <i>en silencio</i> convierte un
    /// error de arranque en una caída difícil de diagnosticar, con los usuarios reportando que
    /// "no tienen permiso" y los logs mostrando un arranque normal. Mejor no arrancar.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var addedPermissions = await SyncCatalogAsync(db, cancellationToken);
        var addedGrants = await SyncBaseMatrixAsync(db, cancellationToken);
        var addedRules = await SyncFhirRulesAsync(db, cancellationToken);

        if (addedPermissions > 0 || addedGrants > 0 || addedRules > 0)
        {
            // Cambió la plantilla global: hay que invalidar la caché de todas las instancias.
            await BumpGlobalVersionAsync(db, cancellationToken);
            logger.LogInformation(
                "Matriz de permisos: +{Permissions} permiso(s), +{Grants} concesión(es), +{Rules} regla(s) FHIR.",
                addedPermissions, addedGrants, addedRules);
        }

        // Guarda de arranque: si la matriz quedó vacía, el problema es ahora y no cuando el
        // primer técnico no pueda entrar a la worklist.
        var total = await db.RolePermissions.CountAsync(x => x.OrganizationId == null, cancellationToken);
        if (total == 0)
        {
            throw new InvalidOperationException(
                "La matriz de permisos quedó vacía tras el seed. Sin ella toda la API responde 403; " +
                "se aborta el arranque en vez de servir una instalación inutilizable.");
        }
    }

    private static async Task<int> SyncCatalogAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.Permissions.AsNoTracking()
            .Select(p => p.Code)
            .ToListAsync(ct);
        var known = new HashSet<string>(existing, StringComparer.Ordinal);

        var toAdd = PermissionCatalog.All
            .Where(p => !known.Contains(p.Code))
            .Select(p => Permission.Create(p.Code, p.Module, p.Description))
            .ToList();

        if (toAdd.Count == 0)
        {
            return 0;
        }
        db.Permissions.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        return toAdd.Count;
    }

    private async Task<int> SyncBaseMatrixAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.RolePermissions.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .Select(x => new { x.RoleCode, x.PermissionCode })
            .ToListAsync(ct);
        var known = new HashSet<string>(
            existing.Select(x => $"{x.RoleCode}|{x.PermissionCode}"), StringComparer.Ordinal);

        var toAdd = PermissionCatalog.BaseMatrix
            .Where(m => !known.Contains($"{m.Role}|{m.Permission}"))
            .Select(m => RolePermission.Create(
                m.Role, m.Permission, PermissionScope.Organization, organizationId: null,
                denied: false, timeProvider: timeProvider))
            .ToList();

        if (toAdd.Count == 0)
        {
            return 0;
        }
        db.RolePermissions.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        return toAdd.Count;
    }

    /// <summary>
    /// Lista blanca de acceso FHIR (SEC-3). Se reconcilia igual que la matriz: un recurso nuevo
    /// declarado en el catálogo tiene que llegar a las instalaciones existentes, o la pantalla que
    /// lo usa se rompe con un 403 sin explicación.
    /// </summary>
    private async Task<int> SyncFhirRulesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.FhirAccessRules.AsNoTracking()
            .Where(x => x.OrganizationId == null)
            .Select(x => new { x.ResourceType, x.Verb, x.PermissionCode })
            .ToListAsync(ct);
        var known = new HashSet<string>(
            existing.Select(x => $"{x.ResourceType}|{x.Verb}|{x.PermissionCode}"), StringComparer.Ordinal);

        var toAdd = FhirAccessCatalog.BaseRules
            .Where(r => !known.Contains($"{r.Resource}|{r.Verb}|{r.Permission}"))
            .Select(r => FhirAccessRule.Create(r.Resource, r.Verb, r.Permission, null, timeProvider))
            .ToList();

        if (toAdd.Count == 0)
        {
            return 0;
        }
        db.FhirAccessRules.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
        return toAdd.Count;
    }

    private async Task BumpGlobalVersionAsync(AppDbContext db, CancellationToken ct)
    {
        var row = await db.PolicyVersions
            .FirstOrDefaultAsync(x => x.OrganizationId == PolicyVersion.GlobalScope, ct);
        if (row is null)
        {
            db.PolicyVersions.Add(PolicyVersion.Create(PolicyVersion.GlobalScope, timeProvider));
        }
        else
        {
            row.Bump(null, timeProvider);
        }
        await db.SaveChangesAsync(ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
