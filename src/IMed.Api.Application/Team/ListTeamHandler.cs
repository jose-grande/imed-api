using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Practitioners;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

public sealed record ListTeamQuery(Guid OrganizationId);

/// <summary>
/// Lista los miembros del equipo de la organización del JWT.
///
/// <para>Pipeline:</para>
/// <list type="number">
///   <item>Lookup <c>OrganizationProfile</c> en BD para obtener <c>FhirId</c> + <c>PartitionName</c>.</item>
///   <item>HAPI: search <c>PractitionerRole?organization=Organization/{fhirId}&amp;active=true</c>.</item>
///   <item>Para cada PractitionerRole en paralelo: HAPI <c>GET Practitioner/{id}</c> + KC <c>GET user/{id}</c> (a través de <c>user_practitioner_assignments</c>) + KC <c>GET role-mappings</c>.</item>
///   <item>Merge → <c>TeamMember[]</c>.</item>
/// </list>
///
/// <para><b>Notas operativas.</b> Lookups N+1: para piloto con orgs &lt;30 members
/// es aceptable. Si crece, optimizar con HAPI <c>_include=PractitionerRole:practitioner</c>
/// (un solo Bundle) + batch lookup en BD del repo. Documentado como follow-up.</para>
/// </summary>
public sealed class ListTeamHandler(
    IOrganizationProfileRepository orgRepo,
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
            return Array.Empty<TeamMember>();
        }

        var partitionName = $"org-{query.OrganizationId}";
        var roles = await hapi.SearchPractitionerRolesByOrganizationAsync(org.FhirId, partitionName, cancellationToken);
        if (roles.Count == 0)
        {
            return Array.Empty<TeamMember>();
        }

        // Precargamos TODOS los assignments de la org en una sola query y los
        // pasamos al builder paralelo como dict in-memory. Esto cumple dos cosas:
        // (1) evita N+1 reads, (2) saca al DbContext del path concurrente —
        // EF Core DbContext no es thread-safe y compartirlo en Task.WhenAll
        // generaba "A second operation was started on this context instance"
        // dependiendo del timing, dejando miembros invisibles intermitentemente.
        var assignments = await assignmentRepo.ListByOrganizationAsync(query.OrganizationId, cancellationToken);
        var assignmentsByPractitionerId = assignments.ToDictionary(
            a => a.PractitionerFhirId,
            a => a,
            StringComparer.Ordinal);

        // Sedes de toda la organización en dos lecturas, antes del tramo paralelo: mismo motivo
        // que los assignments, el DbContext no se comparte entre tareas.
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

        var tasks = roles.Select(role => BuildTeamMemberAsync(
            role, assignmentsByPractitionerId, partitionName, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results
            .Where(m => m is not null)
            .Select(m => m! with { Locations = sedesPorUsuario.GetValueOrDefault(m!.KeycloakUserId) ?? [] })
            .ToList();
    }

    private async Task<TeamMember?> BuildTeamMemberAsync(
        PractitionerRoleListItem role,
        Dictionary<string, UserPractitionerAssignment> assignmentsByPractitionerId,
        string partitionName,
        CancellationToken cancellationToken)
    {
        // El assignment es la fuente de verdad del vínculo Practitioner↔KC user.
        // Sin él el row es ruido — lo skipeamos. Cualquier otra fuente (HAPI
        // Practitioner, KC role-mappings) es degradable: un blip transitorio
        // no debe sacar al miembro de la lista.
        if (!assignmentsByPractitionerId.TryGetValue(role.PractitionerFhirId, out var assignment))
        {
            logger.LogWarning(
                "ListTeam: PractitionerRole {RoleId} apunta a Practitioner {PracId} sin UserPractitionerAssignment local; skip",
                role.Id, role.PractitionerFhirId);
            return null;
        }

        // Lookups secundarios en paralelo: HAPI Practitioner + KC realm roles.
        // Ambos son best-effort y no tocan el DbContext.
        var practitionerTask = SafeGetPractitionerAsync(role.PractitionerFhirId, partitionName, cancellationToken);
        var rolesTask = SafeGetUserRealmRolesAsync(assignment.KeycloakUserId, cancellationToken);
        await Task.WhenAll(practitionerTask, rolesTask);

        var practitioner = practitionerTask.Result;
        var realmRoles = rolesTask.Result;

        var primaryRole = RolePriority.FirstOrDefault(r => realmRoles.Contains(r, StringComparer.Ordinal))
            ?? "practitioner";

        return new TeamMember
        {
            KeycloakUserId = assignment.KeycloakUserId,
            Email = practitioner?.Email ?? string.Empty,
            DisplayName = BuildDisplayName(practitioner),
            Role = primaryRole,
            PractitionerFhirId = role.PractitionerFhirId,
            PractitionerRoleFhirId = role.Id,
            SpecialtyCode = role.SpecialtyCode,
            SpecialtyDisplay = role.SpecialtyDisplay,
            IsActive = role.Active,
        };
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
            logger.LogWarning(ex,
                "ListTeam: KC GetUserRealmRoles {UserId} falló; role default 'practitioner'",
                keycloakUserId);
            return Array.Empty<string>();
        }
    }

    private static string BuildDisplayName(PractitionerSnapshot? practitioner)
    {
        if (practitioner is null)
        {
            return "(sin nombre)";
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
        return parts.Count > 0 ? string.Join(" ", parts) : "(sin nombre)";
    }
}
