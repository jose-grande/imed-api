namespace IMed.Api.Application.Team;

/// <summary>
/// DTO unificado de un miembro del equipo de una organización. Combina:
/// <list type="bullet">
///   <item>FHIR <c>PractitionerRole</c> (membership + specialty)</item>
///   <item>FHIR <c>Practitioner</c> (nombre)</item>
///   <item>tabla <c>user_practitioner_assignments</c> (link a Keycloak userId)</item>
///   <item>Keycloak user (email + role realm-level + enabled)</item>
/// </list>
///
/// <para>Backend hace el merge — el frontend recibe el shape ya unificado.</para>
/// </summary>
public sealed record TeamMember
{
    public required Guid KeycloakUserId { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>"org_admin" | "practitioner" | "auxiliar". Si el user tiene varios, se prioriza org_admin > practitioner > auxiliar.</summary>
    public required string Role { get; init; }
    public required string PractitionerFhirId { get; init; }
    public required string PractitionerRoleFhirId { get; init; }
    public string? SpecialtyCode { get; init; }
    public string? SpecialtyDisplay { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Sedes donde trabaja, la principal primero (FAC-9). Nunca vacía para un miembro activo.</summary>
    public IReadOnlyList<TeamMemberLocation> Locations { get; init; } = [];
}

/// <summary>Sede de un miembro del equipo.</summary>
public sealed record TeamMemberLocation(Guid Id, string Code, string Name, bool IsPrimary);
