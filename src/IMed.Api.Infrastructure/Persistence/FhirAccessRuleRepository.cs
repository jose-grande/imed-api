using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Reglas de acceso FHIR con caché local (ADR-007, SEC-3).
///
/// <para>Se consultan en cada request al proxy, así que van cacheadas por la misma razón que la
/// matriz: son kilobytes y cambian rara vez. La clave incluye la versión de política de la
/// organización, para que agregar una regla surta efecto sin esperar el TTL.</para>
/// </summary>
public sealed class FhirAccessRuleRepository(AppDbContext db, IMemoryCache cache) : IFhirAccessRuleRepository
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    public async Task<IReadOnlyList<string>> ListForAsync(
        Guid organizationId, string resourceType, FhirVerb verb, CancellationToken ct = default)
    {
        var version = await db.PolicyVersions.AsNoTracking()
            .Where(x => x.OrganizationId == PolicyVersion.GlobalScope || x.OrganizationId == organizationId)
            .SumAsync(x => x.Version, ct);

        var key = $"fhirrule:{organizationId}:{version}:{resourceType}:{verb}";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        var permissions = await db.FhirAccessRules.AsNoTracking()
            .Where(x => (x.OrganizationId == null || x.OrganizationId == organizationId)
                        && x.ResourceType == resourceType
                        && x.Verb == verb)
            .Select(x => x.PermissionCode)
            .Distinct()
            .ToListAsync(ct);

        cache.Set(key, (IReadOnlyList<string>)permissions, CacheTtl);
        return permissions;
    }
}
