using IMed.Api.Domain.Forms;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia la persistencia de <see cref="FormDefinition"/>.
/// </summary>
public interface IFormDefinitionRepository
{
    Task<FormDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista las definiciones de una org (incluye `published` y `draft`/`retired` si <paramref name="includeNonPublished"/> es true).</summary>
    Task<IReadOnlyList<FormDefinition>> ListAsync(
        Guid? organizationId,
        bool includeNonPublished,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve la mejor definición publicada para una especialidad.
    /// Orden: org-específica → preset global con specialty → preset global "general".
    /// Devuelve null si no hay nada.
    /// </summary>
    Task<FormDefinition?> ResolveBySpecialtyAsync(
        Guid organizationId,
        string? specialtyCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve una definición exacta por (code, version). La usa el detail page
    /// del encuentro para reconstruir cómo se vio el form al momento del cierre,
    /// resolviendo el canonical <c>QR.questionnaire</c>. Acepta cualquier status
    /// (incluído retired) — un encuentro histórico debe poder verse aunque el
    /// form ya no esté vigente. Orden: org-específica → preset global. Devuelve
    /// null si no existe esa version.
    /// </summary>
    Task<FormDefinition?> ResolveByCodeAndVersionAsync(
        Guid organizationId,
        string code,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>Confirma si existe (code, version, organizationId). Para evitar duplicados al crear.</summary>
    Task<bool> ExistsAsync(string code, string version, Guid? organizationId, CancellationToken cancellationToken = default);

    Task AddAsync(FormDefinition form, CancellationToken cancellationToken = default);
    Task UpdateAsync(FormDefinition form, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
