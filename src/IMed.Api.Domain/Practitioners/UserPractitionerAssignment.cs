namespace IMed.Api.Domain.Practitioners;

/// <summary>
/// Vincula un user de Keycloak (`KeycloakUserId`) con su recurso `Practitioner`
/// FHIR dentro de una `Organization`. Es la fuente de verdad de "qué Practitioner
/// representa al user X en la org Y" en `imed_app`.
///
/// <para>El claim `practitioner_id` del JWT se popula desde acá vía outbox →
/// Keycloak admin API. Si el claim está vacío, este registro permite recuperar
/// el id sin pegarle a HAPI.</para>
/// </summary>
public sealed class UserPractitionerAssignment
{
    public Guid KeycloakUserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string PractitionerFhirId { get; private set; } = string.Empty;
    public string? PractitionerRoleFhirId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private UserPractitionerAssignment() { }

    public static UserPractitionerAssignment Create(
        Guid keycloakUserId,
        Guid organizationId,
        string practitionerFhirId,
        string? practitionerRoleFhirId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(practitionerFhirId))
        {
            throw new ArgumentException("PractitionerFhirId es obligatorio.", nameof(practitionerFhirId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new UserPractitionerAssignment
        {
            KeycloakUserId = keycloakUserId,
            OrganizationId = organizationId,
            PractitionerFhirId = practitionerFhirId,
            PractitionerRoleFhirId = practitionerRoleFhirId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public void UpdatePractitionerRole(string practitionerRoleFhirId, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(practitionerRoleFhirId))
        {
            throw new ArgumentException("PractitionerRoleFhirId es obligatorio.", nameof(practitionerRoleFhirId));
        }
        PractitionerRoleFhirId = practitionerRoleFhirId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
