using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Garantiza que toda organización tenga al menos una sede (ADR-008).
///
/// <para>Sin sede no se puede operar nada del LIS: las worklists, los correlativos y las etiquetas
/// cuelgan de ella. Por eso la garantía tiene que ser "existe siempre" y no "existe si el bootstrap
/// salió bien" — de ahí que sea idempotente y que además la invoque un backfill de arranque.</para>
/// </summary>
public sealed class LocationProvisioning(
    AppDbContext db,
    TimeProvider timeProvider,
    ILogger<LocationProvisioning> logger) : ILocationProvisioning
{
    /// <summary>Código de la sede que se crea sola. Estable: lo referencian backfills y pruebas.</summary>
    public const string DefaultCode = "PRINCIPAL";

    public async Task<OrganizationLocation> EnsureDefaultLocationAsync(
        Guid organizationId, string? organizationName = null, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("organizationId es obligatorio.", nameof(organizationId));
        }

        var existing = await db.OrganizationLocations
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct);
        if (existing is not null)
        {
            return existing;
        }

        // La org puede tener sedes sin ninguna marcada como principal (dato viejo, o una
        // desactivación mal hecha). Se promueve la más antigua en vez de crear una nueva:
        // crear otra dejaría la operación repartida entre dos sedes que representan el mismo lugar.
        var orphan = await db.OrganizationLocations
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (orphan is not null)
        {
            orphan.Reactivate(timeProvider);
            orphan.MarkDefault(timeProvider);
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Sede {Code} promovida a principal en la org {OrgId} (no había ninguna marcada).",
                orphan.Code, organizationId);
            return orphan;
        }

        var name = string.IsNullOrWhiteSpace(organizationName) ? "Sede principal" : organizationName!.Trim();
        var legalEntity = await EnsureDefaultLegalEntityAsync(organizationId, organizationName, null, ct);
        var location = OrganizationLocation.Create(
            organizationId, DefaultCode, name, legalEntity.Id, isDefault: true, timeProvider: timeProvider);

        db.OrganizationLocations.Add(location);
        LocationFhirSyncOutbox.EnqueueLocation(db, organizationId, location.Id);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Carrera con otro arranque o con el backfill: los índices únicos de (org, code) y de
            // sede principal hacen que solo una gane. La perdedora relee y devuelve la ganadora.
            db.Entry(location).State = EntityState.Detached;
            var winner = await db.OrganizationLocations
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct);
            if (winner is null)
            {
                throw;
            }
            return winner;
        }

        logger.LogInformation("Sede principal creada para la org {OrgId}.", organizationId);
        return location;
    }

    public async Task<LegalEntity> EnsureDefaultLegalEntityAsync(
        Guid organizationId, string? organizationName = null, string? taxId = null, CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("organizationId es obligatorio.", nameof(organizationId));
        }

        var existing = await db.LegalEntities
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct);
        if (existing is not null)
        {
            return existing;
        }

        // El nombre sale, en orden, del que pasó el llamador, del registrado al contratar, o de
        // un genérico que la pantalla invita a corregir. Ninguno es la razón social legal con
        // certeza: por eso la emisión exige el RTN y no se conforma con que exista la fila.
        var name = organizationName;
        if (string.IsNullOrWhiteSpace(name))
        {
            name = await db.OrganizationSubscriptions.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(ct);
        }

        var entity = LegalEntity.CreateDefault(
            organizationId, string.IsNullOrWhiteSpace(name) ? "Razón social principal" : name!, taxId, timeProvider);
        db.LegalEntities.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Misma carrera que la sede principal: el índice único filtrado deja ganar a una.
            db.Entry(entity).State = EntityState.Detached;
            return await db.LegalEntities
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.IsDefault, ct)
                ?? throw new InvalidOperationException("No se pudo crear la razón social por defecto.");
        }

        logger.LogInformation("Razón social por defecto creada para la org {OrgId}.", organizationId);
        return entity;
    }

    public async Task<bool> EnsureUserAssignedAsync(
        Guid organizationId, Guid userId, Guid? assignedBy = null, CancellationToken ct = default)
    {
        if (await db.UserLocations.AnyAsync(x => x.OrganizationId == organizationId && x.UserId == userId, ct))
        {
            return false;
        }

        var principal = await EnsureDefaultLocationAsync(organizationId, null, ct);
        db.UserLocations.Add(UserLocationAssignment.Create(
            organizationId, userId, principal.Id, isPrimary: true, assignedBy, timeProvider));
        PractitionerLocationsOutbox.Enqueue(db, organizationId, userId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Dos requests del mismo usuario a la vez: la PK (org, user, sede) deja ganar a uno.
            db.ChangeTracker.Clear();
            return false;
        }

        logger.LogInformation(
            "Usuario {UserId} asignado a la sede principal de la org {OrgId}: no tenía ninguna.",
            userId, organizationId);
        return true;
    }
}
