using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Pone al día el catálogo de cobrables de una organización (A2 y A6): las consultas de cada
/// especialidad que tiene algún médico y el cobrable de cada prueba y panel de su laboratorio.
///
/// <para><b>Cuándo corre.</b> Al configurar el régimen fiscal —antes no había tributos y nada se
/// pudo crear— y al arrancar la API, para las organizaciones que ya existían antes de esta
/// funcionalidad. El día a día no lo necesita: cada alta (perfil del médico, invitación, prueba
/// nueva, consulta cobrada) asegura lo suyo.</para>
/// </summary>
public sealed class OrganizationCatalogReconciler(
    IBillableItemProvisioner provisioner,
    IOrganizationProfileRepository organizations,
    IHapiFhirClient hapi,
    ILogger<OrganizationCatalogReconciler> logger)
{
    /// <returns>Especialidades vistas y cobrables de laboratorio creados.</returns>
    public async Task<(int Specialties, int LabItems)> ReconcileAsync(Guid organizationId, CancellationToken ct = default)
    {
        var laboratorio = await provisioner.ReconcileLabAsync(organizationId, ct);

        var especialidades = 0;
        var org = await organizations.GetByIdAsync(organizationId, ct);
        if (org is not null)
        {
            var roles = await hapi.SearchPractitionerRolesByOrganizationAsync(org.FhirId, $"org-{organizationId}", ct);
            foreach (var codigo in roles
                         .Where(r => r.Active && !string.IsNullOrWhiteSpace(r.SpecialtyCode))
                         .Select(r => r.SpecialtyCode!)
                         .Distinct(StringComparer.Ordinal))
            {
                await provisioner.TryEnsureForSpecialtyAsync(organizationId, codigo, logger, ct);
                especialidades++;
            }
        }

        if (laboratorio > 0 || especialidades > 0)
        {
            logger.LogInformation(
                "Catálogo de {Org}: {Specialties} especialidad(es) revisadas, {Lab} cobrable(s) de laboratorio nuevos.",
                organizationId, especialidades, laboratorio);
        }
        return (especialidades, laboratorio);
    }
}
