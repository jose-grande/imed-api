using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Razones sociales de una organización (FAC-9). Lecturas estrictas por organización, igual que
/// las sedes.
/// </summary>
public interface ILegalEntityRepository
{
    Task<IReadOnlyList<LegalEntity>> ListAsync(Guid organizationId, CancellationToken ct = default);

    Task<LegalEntity?> GetAsync(Guid organizationId, Guid legalEntityId, CancellationToken ct = default);

    Task<LegalEntity?> GetDefaultAsync(Guid organizationId, CancellationToken ct = default);

    Task<LegalEntity?> GetByTaxIdAsync(Guid organizationId, string taxId, CancellationToken ct = default);

    Task AddAsync(LegalEntity entity, CancellationToken ct = default);

    Task UpdateAsync(LegalEntity entity, CancellationToken ct = default);

    /// <summary>
    /// ¿Alguna sede de esta razón social ya emitió un documento fiscal? Decide si el RTN todavía
    /// se puede corregir: antes de la primera emisión es un dato mal cargado; después, es historia.
    /// </summary>
    Task<bool> HasIssuedDocumentsAsync(Guid organizationId, Guid legalEntityId, CancellationToken ct = default);
}
