using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>Implementación EF Core de las razones sociales (FAC-9).</summary>
public sealed class LegalEntityRepository(AppDbContext db) : ILegalEntityRepository
{
    public async Task<IReadOnlyList<LegalEntity>> ListAsync(Guid organizationId, CancellationToken ct = default) =>
        await db.LegalEntities.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.LegalName)
            .ToListAsync(ct);

    public Task<LegalEntity?> GetAsync(Guid organizationId, Guid legalEntityId, CancellationToken ct = default) =>
        db.LegalEntities.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == legalEntityId, ct);

    public Task<LegalEntity?> GetDefaultAsync(Guid organizationId, CancellationToken ct = default) =>
        db.LegalEntities.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct);

    public Task<LegalEntity?> GetByTaxIdAsync(Guid organizationId, string taxId, CancellationToken ct = default) =>
        db.LegalEntities.FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.TaxId == taxId, ct);

    public async Task AddAsync(LegalEntity entity, CancellationToken ct = default)
    {
        db.LegalEntities.Add(entity);
        LocationFhirSyncOutbox.EnqueueLegalEntity(db, entity.OrganizationId, entity.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(LegalEntity entity, CancellationToken ct = default)
    {
        db.LegalEntities.Update(entity);
        LocationFhirSyncOutbox.EnqueueLegalEntity(db, entity.OrganizationId, entity.Id);
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> HasIssuedDocumentsAsync(Guid organizationId, Guid legalEntityId, CancellationToken ct = default) =>
        // Por la sede y no solo por la columna del documento: los documentos emitidos antes de
        // FAC-9 no tienen la razón social congelada, pero sí la sede.
        db.FiscalDocuments.AsNoTracking()
            .Where(d => d.OrganizationId == organizationId)
            .AnyAsync(d => d.LegalEntityId == legalEntityId
                || db.OrganizationLocations.Any(l => l.Id == d.LocationId && l.LegalEntityId == legalEntityId), ct);
}
