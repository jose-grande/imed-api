using IMed.Api.Domain.Billing;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Topes de dinero por usuario (FAC-1, hueco G3). Se lee al validar un descuento, una anulación o
/// una diferencia de arqueo.
/// </summary>
public interface IAuthorizationLimitRepository
{
    /// <summary>
    /// El tope que rige para este usuario y esta clase en la fecha dada, o <c>null</c> si no tiene
    /// ninguno.
    ///
    /// <para>Sin tope configurado <b>no</b> significa "sin límite" ni "cero": significa que la
    /// organización no declaró uno, y quien llama decide qué hacer con eso. Devolver 0 haría que
    /// no configurar nada bloqueara todos los descuentos.</para>
    /// </summary>
    Task<UserAuthorizationLimit?> FindEffectiveAsync(
        Guid organizationId,
        Guid userId,
        AuthorizationLimitKind kind,
        DateOnly day,
        CancellationToken ct = default);

    Task<IReadOnlyList<UserAuthorizationLimit>> ListForUserAsync(
        Guid organizationId, Guid userId, CancellationToken ct = default);

    Task AddAsync(UserAuthorizationLimit limit, CancellationToken ct = default);
    Task UpdateAsync(UserAuthorizationLimit limit, CancellationToken ct = default);
}
