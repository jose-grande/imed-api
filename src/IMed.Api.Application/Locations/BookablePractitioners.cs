using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Locations;

/// <summary>
/// Con quién se puede agendar una cita.
///
/// <list type="bullet">
///   <item><b>Médico</b> (rol <c>practitioner</c>) con perfil clínico: siempre.</item>
///   <item><b>Administrador</b> (<c>org_admin</c>) con perfil clínico: solo si atiende pacientes
///   (<see cref="Domain.Organizations.OrganizationMember.SeesPatients"/>), que se marca en Mi
///   equipo. El rol principal es uno solo: el dueño de una clínica chica que además atiende es
///   <c>org_admin</c>, no <c>org_admin</c> + <c>practitioner</c>. Y tener perfil clínico no
///   alcanza, porque el portal invitaba a todos a completar "Mi perfil profesional".</item>
///   <item>Nadie más: el laboratorio y el auxiliar también tienen Practitioner (firman
///   resultados, registran signos), pero no se les da cita.</item>
/// </list>
///
/// <para><b>Administrador sin marca todavía</b> (miembros de antes de la marca): se infiere de su
/// perfil profesional — atiende si cargó especialidad o número de colegiación. Al marcarlo en Mi
/// equipo, la marca manda.</para>
///
/// <para><b>El rol vive en Keycloak</b> y se consulta usuario por usuario: el listado por rol
/// abarca todo el realm (todas las organizaciones) y viene paginado. Si Keycloak o HAPI no
/// responden, se deja pasar con un warning: una agenda que no deja elegir médico ni guardar es
/// peor que un caso de más.</para>
///
/// <para>Lo usan el selector de médico de la agenda y la validación del proxy al guardar la cita,
/// para que los dos apliquen la misma regla.</para>
/// </summary>
public sealed class BookablePractitioners(
    IKeycloakAdminClient keycloak,
    IHapiFhirClient hapi,
    IOrganizationMemberRepository members,
    IUserPractitionerAssignmentRepository assignments,
    ILogger<BookablePractitioners> logger)
{
    /// <summary>
    /// Inferencia para un administrador sin marca: su perfil profesional dice que es médico si
    /// cargó especialidad o número de colegiación.
    /// </summary>
    public static bool ProfileSuggestsDoctor(string? licenseNumber, string? specialtyCode) =>
        !string.IsNullOrWhiteSpace(licenseNumber) || !string.IsNullOrWhiteSpace(specialtyCode);

    /// <summary>De estos usuarios, los que se pueden agendar. Solo cuentan los que tienen perfil clínico.</summary>
    public async Task<IReadOnlySet<Guid>> FilterAsync(
        Guid organizationId, IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        // Lecturas de base antes del tramo paralelo: el DbContext no es thread-safe.
        var marcas = (await members.ListByOrganizationAsync(organizationId, ct))
            .ToDictionary(m => m.KeycloakUserId, m => m.SeesPatients);
        var perfiles = (await assignments.ListByOrganizationAsync(organizationId, ct))
            .ToDictionary(a => a.KeycloakUserId);

        var partition = $"org-{organizationId}";
        var resultados = await Task.WhenAll(userIds.Distinct().Select(async userId =>
        {
            if (!perfiles.TryGetValue(userId, out var perfil))
            {
                return (userId, ok: false);
            }
            var ok = await IsBookableAsync(
                userId, marcas.GetValueOrDefault(userId), perfil.PractitionerFhirId, perfil.PractitionerRoleFhirId,
                partition, ct);
            return (userId, ok);
        }));
        return resultados.Where(r => r.ok).Select(r => r.userId).ToHashSet();
    }

    /// <summary>
    /// ¿Se le puede agendar una cita a este Practitioner? Null si no es un usuario de la
    /// organización (un médico externo, por ejemplo): no hay rol que mirar.
    /// </summary>
    public async Task<bool?> IsBookablePractitionerAsync(
        Guid organizationId, string practitionerFhirId, CancellationToken ct = default)
    {
        var perfil = await assignments.GetByPractitionerFhirIdAsync(practitionerFhirId, organizationId, ct);
        if (perfil is null)
        {
            return null;
        }
        return (await FilterAsync(organizationId, [perfil.KeycloakUserId], ct)).Contains(perfil.KeycloakUserId);
    }

    private async Task<bool> IsBookableAsync(
        Guid userId, bool? seesPatients, string practitionerFhirId, string? practitionerRoleFhirId,
        string partition, CancellationToken ct)
    {
        IReadOnlyList<string> roles;
        try
        {
            roles = await keycloak.GetUserRealmRolesAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agenda: no se pudieron leer los roles de {UserId}; se lo acepta igual", userId);
            return true;
        }

        if (roles.Contains(InvitationRoleNames.Practitioner, StringComparer.Ordinal))
        {
            return true;
        }
        if (!roles.Contains(InvitationRoleNames.OrgAdmin, StringComparer.Ordinal))
        {
            return false;
        }
        if (seesPatients is { } marca)
        {
            return marca;
        }

        try
        {
            var practitioner = await hapi.GetPractitionerAsync(practitionerFhirId, partition, ct);
            var rol = practitionerRoleFhirId is null
                ? null
                : await hapi.GetPractitionerRoleAsync(practitionerRoleFhirId, partition, ct);
            return ProfileSuggestsDoctor(practitioner?.LicenseNumber, rol?.SpecialtyCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agenda: no se pudo leer el perfil de {UserId}; se lo acepta igual", userId);
            return true;
        }
    }
}
