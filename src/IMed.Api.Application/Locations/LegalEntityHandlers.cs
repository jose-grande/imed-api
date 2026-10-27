using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Locations;

/// <summary>Razón social tal como la ve la interfaz (FAC-9).</summary>
/// <param name="LocationCount">Cuántas sedes emiten con ella.</param>
/// <param name="TaxIdLocked">
/// El RTN ya no se puede corregir porque alguna de sus sedes emitió. Cambiarlo reescribiría el
/// emisor de documentos que ya salieron con el anterior.
/// </param>
public sealed record LegalEntityDto(
    Guid Id,
    string LegalName,
    string? TradeName,
    string? TaxId,
    string? FiscalAddress,
    string? Phone,
    string? Email,
    bool IsDefault,
    bool Active,
    int LocationCount,
    bool TaxIdLocked);

public sealed class ListLegalEntitiesHandler(
    ILegalEntityRepository legalEntities, ILocationRepository locations, ILocationProvisioning provisioning)
{
    public async Task<IReadOnlyList<LegalEntityDto>> HandleAsync(Guid organizationId, CancellationToken ct = default)
    {
        var razones = await legalEntities.ListAsync(organizationId, ct);
        if (razones.Count == 0)
        {
            // Misma garantía que la sede: la organización siempre tiene con quién emitir.
            await provisioning.EnsureDefaultLegalEntityAsync(organizationId, null, null, ct);
            razones = await legalEntities.ListAsync(organizationId, ct);
        }

        var sedes = await locations.ListAsync(organizationId, activeOnly: false, ct);
        var result = new List<LegalEntityDto>(razones.Count);
        foreach (var r in razones)
        {
            var locked = await legalEntities.HasIssuedDocumentsAsync(organizationId, r.Id, ct);
            result.Add(ToDto(r, sedes.Count(s => s.LegalEntityId == r.Id), locked));
        }
        return result;
    }

    internal static LegalEntityDto ToDto(LegalEntity r, int locationCount, bool taxIdLocked) =>
        new(r.Id, r.LegalName, r.TradeName, r.TaxId, r.FiscalAddress, r.Phone, r.Email,
            r.IsDefault, r.Active, locationCount, taxIdLocked);
}

public sealed record UpdateLegalEntityCommand(
    Guid OrganizationId,
    Guid LegalEntityId,
    string LegalName,
    string? TaxId,
    string? FiscalAddress,
    string? TradeName,
    string? Phone,
    string? Email);

/// <summary>
/// Corrige los datos fiscales de una razón social. El RTN solo mientras ninguna de sus sedes haya
/// emitido: después, un RTN distinto es otra persona jurídica y se registra como tal.
/// </summary>
public sealed class UpdateLegalEntityHandler(
    ILegalEntityRepository legalEntities, ILocationRepository locations, TimeProvider timeProvider)
{
    public async Task<LegalEntityDto?> HandleAsync(UpdateLegalEntityCommand cmd, CancellationToken ct = default)
    {
        var razon = await legalEntities.GetAsync(cmd.OrganizationId, cmd.LegalEntityId, ct);
        if (razon is null)
        {
            return null;
        }

        var locked = await legalEntities.HasIssuedDocumentsAsync(cmd.OrganizationId, razon.Id, ct);
        if (razon.WouldChangeTaxId(cmd.TaxId))
        {
            if (locked)
            {
                throw new InvalidOperationException(
                    "Esta razón social ya emitió documentos con su RTN actual. Si el RTN cambió, " +
                    "es otra persona jurídica: registrala como nueva y pasale las sedes.");
            }

            if (!string.IsNullOrWhiteSpace(cmd.TaxId))
            {
                var normalizado = LegalEntity.NormalizeTaxId(cmd.TaxId);
                var otra = await legalEntities.GetByTaxIdAsync(cmd.OrganizationId, normalizado, ct);
                if (otra is not null && otra.Id != razon.Id)
                {
                    throw new InvalidOperationException(
                        $"El RTN ya pertenece a la razón social '{otra.LegalName}'.");
                }
            }
        }

        razon.Update(cmd.LegalName, cmd.TaxId, cmd.FiscalAddress, cmd.TradeName, cmd.Phone, cmd.Email, timeProvider);
        await legalEntities.UpdateAsync(razon, ct);

        var sedes = await locations.ListAsync(cmd.OrganizationId, activeOnly: false, ct);
        return ListLegalEntitiesHandler.ToDto(razon, sedes.Count(s => s.LegalEntityId == razon.Id), locked);
    }
}
