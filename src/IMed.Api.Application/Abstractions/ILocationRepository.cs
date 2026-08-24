using IMed.Api.Domain.Locations;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Acceso a las sedes de una organización y a las asignaciones de usuario (ADR-008).
///
/// <para>Todas las lecturas son estrictas por organización: una sede pertenece a una y solo una,
/// y no hay operación legítima que cruce el límite.</para>
/// </summary>
public interface ILocationRepository
{
    /// <summary>Sedes de la organización. <paramref name="activeOnly"/> filtra las desactivadas.</summary>
    Task<IReadOnlyList<OrganizationLocation>> ListAsync(
        Guid organizationId, bool activeOnly = true, CancellationToken ct = default);

    Task<OrganizationLocation?> GetAsync(Guid organizationId, Guid locationId, CancellationToken ct = default);

    Task<OrganizationLocation?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default);

    /// <summary>
    /// Sede por defecto de la organización. Es el ancla del backfill y el fallback cuando el
    /// usuario no eligió ninguna.
    /// </summary>
    Task<OrganizationLocation?> GetDefaultAsync(Guid organizationId, CancellationToken ct = default);

    Task AddAsync(OrganizationLocation location, CancellationToken ct = default);

    Task UpdateAsync(OrganizationLocation location, CancellationToken ct = default);

    /// <summary>Sedes en las que el usuario puede operar, con su asignación.</summary>
    Task<IReadOnlyList<(UserLocationAssignment Assignment, OrganizationLocation Location)>> ListForUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default);

    Task<UserLocationAssignment?> GetAssignmentAsync(
        Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default);

    Task AddAssignmentAsync(UserLocationAssignment assignment, CancellationToken ct = default);

    Task RemoveAssignmentAsync(
        Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default);

    /// <summary>Deja una sola sede marcada como primaria para el usuario.</summary>
    Task SetPrimaryAsync(Guid organizationId, Guid userId, Guid locationId, CancellationToken ct = default);
}

/// <summary>
/// Aprovisionamiento de la sede por defecto de una organización.
///
/// <para>Mismo contrato resiliente que el catálogo de laboratorio: se invoca en el bootstrap y, si
/// falla, un backfill de arranque lo recupera. Una organización sin sede no puede operar nada del
/// LIS, así que la garantía tiene que ser "existe siempre", no "existe si el bootstrap salió bien".</para>
/// </summary>
public interface ILocationProvisioning
{
    /// <summary>
    /// Crea la sede principal si la organización no tiene ninguna. Idempotente. Devuelve la sede
    /// por defecto, exista ya o se acabe de crear.
    /// </summary>
    Task<OrganizationLocation> EnsureDefaultLocationAsync(
        Guid organizationId, string? organizationName = null, CancellationToken ct = default);
}
