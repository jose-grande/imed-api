using IMed.Api.Domain.Authorization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Organizations;
using IMed.Api.Domain.Practitioners;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record ListTeamQuery(Guid OrganizationId);

/// <summary>
/// Lista los miembros de la organización del JWT.
///
/// <para><b>Fuente:</b> <see cref="OrganizationMember"/>, que se registra al iniciar sesión.
/// Antes la lista salía de los PractitionerRole de HAPI, así que quien no tenía Practitioner
/// —administración, caja, recepción, cuentas creadas desde la consola de Keycloak— no aparecía
/// ni se le podían asignar sedes (observación de staging).</para>
///
/// <para>Encima de la membresía se agregan, cuando existen: el perfil clínico (Practitioner y su
/// especialidad, solo roles clínicos y de laboratorio), los roles de Keycloak y las sedes. Esas
/// fuentes son degradables: si HAPI o Keycloak fallan, el miembro aparece igual, con menos
/// datos.</para>
/// </summary>
public sealed class ListTeamHandler(
    IOrganizationProfileRepository orgRepo,
    IOrganizationMemberRepository memberRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    IHapiFhirClient hapi,
    IKeycloakAdminClient keycloak,
    ILogger<ListTeamHandler> logger,
    ILocationRepository locations)
{
    private static readonly string[] RolePriority = ["org_admin", "practitioner", "auxiliar"];

    public async Task<IReadOnlyList<TeamMember>> HandleAsync(
        ListTeamQuery query,
        CancellationToken cancellationToken = default)
    {
        var org = await orgRepo.GetByIdAsync(query.OrganizationId, cancellationToken);
        if (org is null)
        {
            logger.LogWarning("ListTeam: organization {OrgId} no existe", query.OrganizationId);
            return [];
        }

        // Todas las lecturas de BD antes del tramo paralelo: el DbContext no es thread-safe.
        var members = await memberRepo.ListByOrganizationAsync(query.OrganizationId, cancellationToken);
        if (members.Count == 0)
        {
            return [];
        }
        var assignments = (await assignmentRepo.ListByOrganizationAsync(query.OrganizationId, cancellationToken))
            .ToDictionary(a => a.KeycloakUserId);

        var sedes = (await locations.ListAsync(query.OrganizationId, activeOnly: true, cancellationToken))
            .ToDictionary(l => l.Id);
        var sedesPorUsuario = (await locations.ListOrganizationAssignmentsAsync(query.OrganizationId, cancellationToken))
            .Where(a => sedes.ContainsKey(a.LocationId))
            .GroupBy(a => a.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<TeamMemberLocation>)g
                    .OrderByDescending(a => a.IsPrimary)
                    .ThenBy(a => sedes[a.LocationId].Name)
                    .Select(a => new TeamMemberLocation(
                        a.LocationId, sedes[a.LocationId].Code, sedes[a.LocationId].Name, a.IsPrimary))
                    .ToList());

        // Especialidad de los que tienen perfil clínico: una búsqueda para toda la organización.
        var partitionName = $"org-{query.OrganizationId}";
        var rolesByPractitioner = await SafeSearchRolesAsync(org.FhirId, partitionName, cancellationToken);

        var tasks = members.Select(m => BuildAsync(
            m,
            assignments.GetValueOrDefault(m.KeycloakUserId),
            rolesByPractitioner,
            sedesPorUsuario.GetValueOrDefault(m.KeycloakUserId) ?? [],
            partitionName,
            cancellationToken));
        var team = await Task.WhenAll(tasks);

        return team
            .OrderByDescending(t => t.IsActive)
            .ThenBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private async Task<TeamMember> BuildAsync(
        OrganizationMember member,
        UserPractitionerAssignment? assignment,
        IReadOnlyDictionary<string, PractitionerRoleListItem> rolesByPractitioner,
        IReadOnlyList<TeamMemberLocation> locations,
        string partitionName,
        CancellationToken cancellationToken)
    {
        var practitionerTask = assignment is null
            ? Task.FromResult<PractitionerSnapshot?>(null)
            : SafeGetPractitionerAsync(assignment.PractitionerFhirId, partitionName, cancellationToken);
        var rolesTask = SafeGetUserRealmRolesAsync(member.KeycloakUserId, cancellationToken);
        await Task.WhenAll(practitionerTask, rolesTask);

        var practitioner = practitionerTask.Result;
        var realmRoles = rolesTask.Result;
        var practitionerRole = assignment is null
            ? null
            : rolesByPractitioner.GetValueOrDefault(assignment.PractitionerFhirId);

        // Sin roles legibles (Keycloak caído) no se inventa uno: se muestra el más bajo.
        var primaryRole = RolePriority.FirstOrDefault(r => realmRoles.Contains(r, StringComparer.Ordinal))
            ?? "auxiliar";

        return new TeamMember
        {
            KeycloakUserId = member.KeycloakUserId,
            Email = member.Email ?? practitioner?.Email ?? string.Empty,
            DisplayName = member.DisplayName
                ?? BuildDisplayName(practitioner)
                ?? member.Email
                ?? "(sin nombre)",
            Role = primaryRole,
            PractitionerFhirId = assignment?.PractitionerFhirId,
            PractitionerRoleFhirId = assignment?.PractitionerRoleFhirId,
            SpecialtyCode = practitionerRole?.SpecialtyCode,
            SpecialtyDisplay = practitionerRole?.SpecialtyDisplay,
            IsActive = member.IsActive,
            LastSeenAt = member.LastSeenAt,
            Locations = locations,
            Functions = FunctionalRoles.All.Where(f => realmRoles.Contains(f, StringComparer.Ordinal)).ToList(),
        };
    }

    private async Task<IReadOnlyDictionary<string, PractitionerRoleListItem>> SafeSearchRolesAsync(
        string organizationFhirId, string partitionName, CancellationToken cancellationToken)
    {
        try
        {
            var roles = await hapi.SearchPractitionerRolesByOrganizationAsync(organizationFhirId, partitionName, cancellationToken);
            return roles
                .GroupBy(r => r.PractitionerFhirId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ListTeam: HAPI PractitionerRole search falló; equipo sin especialidades");
            return new Dictionary<string, PractitionerRoleListItem>(StringComparer.Ordinal);
        }
    }

    private async Task<PractitionerSnapshot?> SafeGetPractitionerAsync(
        string practitionerFhirId, string partitionName, CancellationToken cancellationToken)
    {
        try
        {
            return await hapi.GetPractitionerAsync(practitionerFhirId, partitionName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "ListTeam: HAPI GetPractitioner {PracId} falló; miembro con datos parciales",
                practitionerFhirId);
            return null;
        }
    }

    private async Task<IReadOnlyList<string>> SafeGetUserRealmRolesAsync(
        Guid keycloakUserId, CancellationToken cancellationToken)
    {
        try
        {
            return await keycloak.GetUserRealmRolesAsync(keycloakUserId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "ListTeam: KC GetUserRealmRoles {UserId} falló", keycloakUserId);
            return [];
        }
    }

    private static string? BuildDisplayName(PractitionerSnapshot? practitioner)
    {
        if (practitioner is null)
        {
            return null;
        }
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(practitioner.Prefix))
        {
            parts.Add(practitioner.Prefix);
        }
        parts.AddRange(practitioner.Given.Where(g => !string.IsNullOrWhiteSpace(g)));
        if (!string.IsNullOrWhiteSpace(practitioner.Family))
        {
            parts.Add(practitioner.Family);
        }
        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }
}
