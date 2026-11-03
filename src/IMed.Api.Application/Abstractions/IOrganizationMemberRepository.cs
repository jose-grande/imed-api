using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Abstractions;

/// <summary>Miembros de una organización: la fuente de verdad de la pertenencia.</summary>
public interface IOrganizationMemberRepository
{
    Task<OrganizationMember?> GetAsync(
        Guid keycloakUserId, Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>Todos los miembros de la organización (activos e inactivos), en una sola lectura.</summary>
    Task<IReadOnlyList<OrganizationMember>> ListByOrganizationAsync(
        Guid organizationId, CancellationToken cancellationToken = default);

    Task AddAsync(OrganizationMember member, CancellationToken cancellationToken = default);

    Task UpdateAsync(OrganizationMember member, CancellationToken cancellationToken = default);
}
