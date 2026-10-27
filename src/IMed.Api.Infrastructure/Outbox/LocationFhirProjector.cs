using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Proyecta sedes y razones sociales a HAPI (FAC-9): la razón social como <c>Organization</c>
/// <c>partOf</c> el tenant, la sede como <c>Location</c> con <c>managingOrganization</c> a su razón
/// social. Todo en la partición del tenant: no se abre ninguna partición nueva (ADR-008).
///
/// <para><b>Idempotente.</b> Relee el estado actual y escribe con <c>PUT</c> sobre ids
/// determinísticos. Al proyectar una sede escribe primero su razón social, porque HAPI valida la
/// integridad referencial al escribir y el <c>Location</c> la referencia.</para>
/// </summary>
public sealed class LocationFhirProjector(
    AppDbContext db,
    IHapiFhirClient hapi,
    TimeProvider timeProvider,
    ILogger<LocationFhirProjector> logger)
{
    /// <summary>Proyecta la sede y su razón social. Devuelve false si no hay qué proyectar.</summary>
    public async Task<bool> ProjectLocationAsync(Guid organizationId, Guid locationId, CancellationToken ct)
    {
        var tenantFhirId = await TenantFhirIdAsync(organizationId, ct);
        if (tenantFhirId is null)
        {
            return false;
        }

        var sede = await db.OrganizationLocations
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == locationId, ct);
        if (sede is null)
        {
            return false;
        }
        var razon = await db.LegalEntities.FirstAsync(x => x.Id == sede.LegalEntityId, ct);
        var partition = $"org-{organizationId}";

        await UpsertLegalEntityAsync(razon, tenantFhirId, partition, ct);

        var fhirId = OrganizationLocation.FhirIdFor(sede.Id);
        await hapi.UpsertLocationAsync(new UpsertLocationRequest(
            PartitionName: partition,
            FhirId: fhirId,
            ManagingOrganizationFhirId: LegalEntity.FhirIdFor(razon.Id),
            Code: sede.Code,
            Name: sede.Name,
            FiscalEstablishmentCode: sede.FiscalEstablishmentCode,
            Address: sede.Address,
            Phone: sede.Phone,
            Active: sede.Active), ct);
        if (sede.LocationFhirId != fhirId)
        {
            sede.AttachFhirLocation(fhirId, timeProvider);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ProjectLegalEntityAsync(Guid organizationId, Guid legalEntityId, CancellationToken ct)
    {
        var tenantFhirId = await TenantFhirIdAsync(organizationId, ct);
        if (tenantFhirId is null)
        {
            return false;
        }
        var razon = await db.LegalEntities
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId && x.Id == legalEntityId, ct);
        if (razon is null)
        {
            return false;
        }

        await UpsertLegalEntityAsync(razon, tenantFhirId, $"org-{organizationId}", ct);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<string?> TenantFhirIdAsync(Guid organizationId, CancellationToken ct)
    {
        var tenantFhirId = await db.OrganizationSubscriptions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.FhirId)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(tenantFhirId))
        {
            // Sin Organization tenant no hay a qué colgar la razón social. No es transitorio:
            // reintentarlo no lo va a hacer aparecer.
            logger.LogWarning(
                "Proyección FHIR de sede omitida: la org {OrgId} no tiene Organization FHIR.", organizationId);
            return null;
        }
        return tenantFhirId;
    }

    private async Task UpsertLegalEntityAsync(
        LegalEntity razon, string tenantFhirId, string partition, CancellationToken ct)
    {
        var fhirId = LegalEntity.FhirIdFor(razon.Id);
        await hapi.UpsertLegalEntityOrganizationAsync(new UpsertLegalEntityOrganizationRequest(
            PartitionName: partition,
            FhirId: fhirId,
            TenantOrganizationFhirId: tenantFhirId,
            LegalName: razon.LegalName,
            TradeName: razon.TradeName,
            TaxId: razon.TaxId,
            FiscalAddress: razon.FiscalAddress,
            Phone: razon.Phone,
            Email: razon.Email,
            Active: razon.Active), ct);

        if (razon.OrganizationFhirId != fhirId)
        {
            razon.AttachFhirOrganization(fhirId, timeProvider);
        }
    }
}
