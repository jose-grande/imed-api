using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;

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

    /// <summary>Asignaciones del usuario, activas o no las sedes.</summary>
    Task<IReadOnlyList<UserLocationAssignment>> ListAssignmentsAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default);

    /// <summary>Asignaciones de toda la organización, para listar el equipo con sus sedes.</summary>
    Task<IReadOnlyList<UserLocationAssignment>> ListOrganizationAssignmentsAsync(
        Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// Reemplaza en una sola transacción las sedes del usuario por <paramref name="locationIds"/>,
    /// con <paramref name="primaryLocationId"/> como principal. Las reglas las valida el handler.
    /// </summary>
    Task ReplaceAssignmentsAsync(
        Guid organizationId, Guid userId, IReadOnlyList<Guid> locationIds, Guid primaryLocationId,
        Guid? assignedBy, CancellationToken ct = default);

    /// <summary>
    /// ¿El profesional <paramref name="practitionerFhirId"/> atiende en la sede? Cruza el
    /// <c>Practitioner</c> con su usuario y sus asignaciones. Null si el profesional no es un
    /// usuario de la organización (un médico externo, por ejemplo): no hay asignación que mirar.
    /// </summary>
    Task<bool?> IsPractitionerAssignedAsync(
        Guid organizationId, string practitionerFhirId, Guid locationId, CancellationToken ct = default);

    /// <summary>Ids FHIR de los profesionales asignados a la sede.</summary>
    Task<IReadOnlyList<string>> ListPractitionerIdsAsync(
        Guid organizationId, Guid locationId, CancellationToken ct = default);
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

    /// <summary>
    /// Crea la razón social por defecto si la organización no tiene (FAC-9). Idempotente. El RTN
    /// solo se usa al crearla: si ya existe, no se toca.
    /// </summary>
    Task<LegalEntity> EnsureDefaultLegalEntityAsync(
        Guid organizationId, string? organizationName = null, string? taxId = null, CancellationToken ct = default);

    /// <summary>
    /// Garantiza que el usuario tenga al menos una sede (FAC-9): si no tiene ninguna, lo asigna a
    /// la sede principal como su principal. Idempotente. Devuelve true si tuvo que asignarlo.
    /// </summary>
    Task<bool> EnsureUserAssignedAsync(
        Guid organizationId, Guid userId, Guid? assignedBy = null, CancellationToken ct = default);
}
