using IMed.Api.Domain.Practitioners;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia la persistencia de <see cref="UserPractitionerAssignment"/>.
/// La PK es el par <c>(KeycloakUserId, OrganizationId)</c>: un mismo user puede
/// tener Practitioner distinto en orgs distintas.
/// </summary>
public interface IUserPractitionerAssignmentRepository
{
    Task<UserPractitionerAssignment?> GetAsync(
        Guid keycloakUserId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup inverso por PractitionerFhirId. Usado por <c>ListTeamHandler</c>
    /// (Épica F2) para hidratar un PractitionerRole devuelto por HAPI con su
    /// keycloakUserId asociado en BD. Devuelve null si no hay assignment para
    /// ese practitioner en la org indicada.
    /// </summary>
    Task<UserPractitionerAssignment?> GetByPractitionerFhirIdAsync(
        string practitionerFhirId,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista todos los assignments de una organización en una sola query.
    /// Usado por <c>ListTeamHandler</c> para evitar N+1 y, sobre todo, para
    /// no compartir el <c>DbContext</c> entre llamados paralelos (EF Core
    /// DbContext no es thread-safe — el lookup-by-PR adentro de un Task.WhenAll
    /// generaba <c>InvalidOperationException</c> "A second operation was
    /// started on this context instance").
    /// </summary>
    Task<IReadOnlyList<UserPractitionerAssignment>> ListByOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task AddAsync(UserPractitionerAssignment assignment, CancellationToken cancellationToken = default);

    Task UpdateAsync(UserPractitionerAssignment assignment, CancellationToken cancellationToken = default);
}
