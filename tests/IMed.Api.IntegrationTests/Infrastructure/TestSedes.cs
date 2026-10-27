using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;
using IMed.Api.Infrastructure.Persistence;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Sede de prueba con su razón social (FAC-9). Desde que la sede tiene FK a <c>legal_entity</c>,
/// crearla a mano exige que la razón social exista; esto deja ambas en el contexto, listas para el
/// <c>SaveChanges</c> del llamador.
/// </summary>
internal static class TestSedes
{
    public static OrganizationLocation Create(
        AppDbContext db, Guid orgId, string code, string name, bool isDefault = false, string? taxId = null)
    {
        var razon = db.LegalEntities.Local.FirstOrDefault(r => r.OrganizationId == orgId && r.IsDefault)
            ?? db.LegalEntities.FirstOrDefault(r => r.OrganizationId == orgId && r.IsDefault);
        if (razon is null)
        {
            razon = LegalEntity.CreateDefault(orgId, "Clínica de Prueba S. de R.L.", taxId);
            db.LegalEntities.Add(razon);
        }
        return OrganizationLocation.Create(orgId, code, name, razon.Id, isDefault: isDefault);
    }
}
