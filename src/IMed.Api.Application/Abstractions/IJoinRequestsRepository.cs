using IMed.Api.Domain.JoinRequests;

namespace IMed.Api.Application.Abstractions;

public interface IJoinRequestsRepository
{
    Task<JoinRequest?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<JoinRequest?> GetPendingByEmailAndOrganizationAsync(
        string email,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JoinRequest>> ListPendingForOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task AddAsync(JoinRequest joinRequest, CancellationToken cancellationToken = default);

    Task UpdateAsync(JoinRequest joinRequest, CancellationToken cancellationToken = default);
}
