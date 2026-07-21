using IMed.Api.Domain.Diagrams;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia la persistencia de <see cref="DiagramDefinition"/>.
/// </summary>
public interface IDiagramDefinitionRepository
{
    Task<DiagramDefinition?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Lista las definiciones de una org (incluye `published` y `draft`/`retired` si <paramref name="includeNonPublished"/> es true).</summary>
    Task<IReadOnlyList<DiagramDefinition>> ListAsync(
        Guid? organizationId,
        bool includeNonPublished,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve TODOS los diagramas publicados aplicables a una especialidad —
    /// a diferencia de forms (un único resultado), una especialidad puede tener
    /// varios diagramas activos. Prioriza la versión org-específica sobre el
    /// preset global cuando ambos comparten <c>code</c> (dedup por code).
    /// </summary>
    Task<IReadOnlyList<DiagramDefinition>> ResolveBySpecialtyAsync(
        Guid organizationId,
        string? specialtyCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resuelve una definición exacta por (code, version), cualquier status —
    /// para reconstruir cómo se vio el diagrama en un encuentro histórico.
    /// Orden: org-específica → preset global.
    /// </summary>
    Task<DiagramDefinition?> ResolveByCodeAndVersionAsync(
        Guid organizationId,
        string code,
        string version,
        CancellationToken cancellationToken = default);

    /// <summary>Confirma si existe (code, version, organizationId). Para evitar duplicados al crear.</summary>
    Task<bool> ExistsAsync(string code, string version, Guid? organizationId, CancellationToken cancellationToken = default);

    Task AddAsync(DiagramDefinition diagram, CancellationToken cancellationToken = default);
    Task UpdateAsync(DiagramDefinition diagram, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
