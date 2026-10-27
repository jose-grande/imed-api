using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Billing;

/// <summary>Razón social de un reporte y las sedes que emiten con ella.</summary>
/// <param name="LegalEntity">Null si la organización todavía no tiene ninguna: el reporte sale vacío.</param>
public sealed record LegalEntityReportScope(LegalEntity? LegalEntity, IReadOnlySet<Guid> LocationIds)
{
    /// <summary>¿El documento es de esta razón social? Los anteriores a FAC-9 no la congelaron: va por su sede.</summary>
    public bool Owns(Guid? legalEntityId, Guid locationId) =>
        LegalEntity is not null
        && (legalEntityId is { } id ? id == LegalEntity.Id : LocationIds.Contains(locationId));
}

/// <summary>
/// Decide de qué razón social es un reporte fiscal (FAC-9, D7).
///
/// <para>Cada razón social es un contribuyente distinto ante el SAR: declara su propio ISV y
/// cuadra su propia caja. Un reporte que sumara dos RTN no le sirve a ninguno de los dos. Si la
/// organización tiene una sola razón social se usa esa sin preguntar; si tiene varias, elegirla es
/// obligatorio.</para>
/// </summary>
public sealed class LegalEntityScopeResolver(ILegalEntityRepository legalEntities, ILocationRepository locations)
{
    /// <exception cref="ArgumentException">Hay varias razones sociales y no se eligió, o la elegida no existe.</exception>
    public async Task<LegalEntityReportScope> ResolveAsync(Guid orgId, Guid? legalEntityId, CancellationToken ct)
    {
        var razones = await legalEntities.ListAsync(orgId, ct);
        LegalEntity? razon;
        if (razones.Count == 0 && legalEntityId is null)
        {
            // Organización sin razón social (no factura todavía): nada que reportar.
            return new LegalEntityReportScope(null, new HashSet<Guid>());
        }
        if (legalEntityId is { } id)
        {
            razon = razones.FirstOrDefault(r => r.Id == id)
                ?? throw new ArgumentException("La razón social no existe en esta organización.");
        }
        else if (razones.Count == 1)
        {
            razon = razones[0];
        }
        else
        {
            throw new ArgumentException(
                "La organización tiene varias razones sociales: elegí de cuál es el reporte. " +
                "Cada una declara por separado.");
        }

        var sedes = (await locations.ListAsync(orgId, activeOnly: false, ct))
            .Where(l => l.LegalEntityId == razon.Id)
            .Select(l => l.Id)
            .ToHashSet();
        return new LegalEntityReportScope(razon, sedes);
    }
}
