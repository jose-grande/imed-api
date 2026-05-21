using IMed.Api.Domain.Invitations;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia la persistencia de <see cref="Invitation"/>. Granular para que
/// los handlers no carguen todo el aggregate cuando solo necesitan checks.
/// </summary>
public interface IInvitationsRepository
{
    Task<Invitation?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup principal del lazy provision. Devuelve la invitación pending para
    /// el par (email, org). Email se compara case-insensitive — siempre se
    /// guarda lowercase desde la factory.
    /// </summary>
    Task<Invitation?> GetPendingByEmailAndOrganizationAsync(
        string email,
        Guid organizationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Invitation>> ListPendingForOrganizationAsync(
        Guid organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista invitaciones <c>pending</c> creadas antes de <paramref name="threshold"/>.
    /// Usado por <c>MarkInvitationsExpiredHandler</c> para identificar candidates
    /// a expirar tras superar el lifespan del action token KC.
    /// </summary>
    Task<IReadOnlyList<Invitation>> ListPendingCreatedBeforeAsync(
        Guid organizationId,
        DateTimeOffset threshold,
        CancellationToken cancellationToken = default);

    Task AddAsync(Invitation invitation, CancellationToken cancellationToken = default);

    Task UpdateAsync(Invitation invitation, CancellationToken cancellationToken = default);
}
