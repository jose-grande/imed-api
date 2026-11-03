namespace IMed.Api.Domain.Organizations;

/// <summary>Estado de un miembro de la organización.</summary>
public enum OrganizationMemberStatus
{
    Active = 0,
    /// <summary>Dado de baja por un administrador: no puede iniciar sesión en la organización.</summary>
    Inactive = 1,
}

/// <summary>
/// Un usuario que pertenece a una organización. Es la <b>fuente de verdad de la pertenencia</b>:
/// la pantalla Equipo, la asignación de sedes y los controles "¿es de esta organización?" leen
/// de acá.
///
/// <para>Antes la pertenencia se infería del <c>UserPractitionerAssignment</c>, que solo existe
/// para quien tiene un Practitioner FHIR. Quien no lo tenía —un administrador recién creado, el
/// personal no clínico, o una cuenta creada desde la consola de Keycloak— no aparecía en Equipo
/// ni se le podían asignar sedes (observación de staging). El Practitioner pasa a ser un perfil
/// clínico opcional del miembro, que se crea solo para roles clínicos y de laboratorio.</para>
///
/// <para>Se registra al iniciar sesión (<c>POST /api/me/session</c>), a partir del token: el
/// <c>organization_id</c> lo pone Keycloak y solo lo puede asignar un administrador.</para>
/// </summary>
public sealed class OrganizationMember
{
    public Guid KeycloakUserId { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Nombre del token en el último ingreso.</summary>
    public string? DisplayName { get; private set; }
    public string? Email { get; private set; }

    public OrganizationMemberStatus Status { get; private set; } = OrganizationMemberStatus.Active;

    public DateTimeOffset JoinedAt { get; private set; }

    /// <summary>Último inicio de sesión. Null en los miembros migrados que todavía no entraron.</summary>
    public DateTimeOffset? LastSeenAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsActive => Status == OrganizationMemberStatus.Active;

    private OrganizationMember() { }

    public static OrganizationMember Join(
        Guid keycloakUserId,
        Guid organizationId,
        string? displayName,
        string? email,
        TimeProvider? timeProvider = null)
    {
        if (keycloakUserId == Guid.Empty)
        {
            throw new ArgumentException("KeycloakUserId es obligatorio.", nameof(keycloakUserId));
        }
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OrganizationMember
        {
            KeycloakUserId = keycloakUserId,
            OrganizationId = organizationId,
            DisplayName = Clean(displayName, 200),
            Email = Clean(email, 320),
            Status = OrganizationMemberStatus.Active,
            JoinedAt = now,
            LastSeenAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Registra un inicio de sesión: refresca nombre y correo con lo que trae el token (Keycloak
    /// es quien los administra) sin pisar lo conocido con vacíos.
    /// </summary>
    public void SeenAt(string? displayName, string? email, TimeProvider? timeProvider = null)
    {
        DisplayName = Clean(displayName, 200) ?? DisplayName;
        Email = Clean(email, 320) ?? Email;
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        LastSeenAt = now;
        UpdatedAt = now;
    }

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Status = OrganizationMemberStatus.Inactive;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Reactivate(TimeProvider? timeProvider = null)
    {
        Status = OrganizationMemberStatus.Active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var t = value.Trim();
        return t.Length > max ? t[..max] : t;
    }
}
