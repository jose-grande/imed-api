namespace IMed.Api.Domain.Invitations;

/// <summary>
/// Invitación de un Org Admin para que un email se incorpore al equipo de su
/// organización. <b>Sin token propio</b> — el token de aceptación lo genera y
/// firma Keycloak vía <c>executeActionsEmail</c>; nuestra tabla solo guarda
/// metadata (a quién, qué role, qué specialty hint, estado).
///
/// <para>Ciclo de vida:
/// <list type="bullet">
///   <item><c>pending</c> → <c>accepted</c> on first login del invitado (lazy
///   detection en <c>GetMyPractitionerProfileHandler</c>).</item>
///   <item><c>pending</c> → <c>revoked</c> si el Org Admin revoca antes que
///   complete el flow.</item>
///   <item><c>pending</c> → <c>expired</c> tras lifespan del action token KC
///   (cleanup manual via endpoint, cron formal en Épica I).</item>
/// </list>
/// Estados terminales son inmutables.</para>
/// </summary>
public sealed class Invitation
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public Guid OrganizationId { get; private set; }
    public string OrganizationFhirId { get; private set; } = string.Empty;
    public InvitationRole Role { get; private set; }

    /// <summary>
    /// Specialty SNOMED hint que se aplicará al crear el PractitionerRole en
    /// el lazy provision. Opcional — si null, el invitee elige al completar
    /// su perfil.
    /// </summary>
    public string? SpecialtyHint { get; private set; }

    /// <summary>
    /// Id del user en Keycloak creado al disparar la invitación. Se popula
    /// inmediatamente tras <c>CreateUserAsync</c> y nunca se borra (sigue
    /// referenciable aunque la invitation expire/revoque).
    /// </summary>
    public Guid? KeycloakUserId { get; private set; }

    public InvitationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? RevokedBy { get; private set; }

    private Invitation() { }

    public static Invitation Create(
        string email,
        Guid organizationId,
        string organizationFhirId,
        InvitationRole role,
        string? specialtyHint,
        Guid createdBy,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email es obligatorio.", nameof(email));
        }
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(organizationFhirId))
        {
            throw new ArgumentException("OrganizationFhirId es obligatorio.", nameof(organizationFhirId));
        }
        if (createdBy == Guid.Empty)
        {
            throw new ArgumentException("CreatedBy es obligatorio.", nameof(createdBy));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new Invitation
        {
            Id = Guid.CreateVersion7(now),
            Email = email.Trim().ToLowerInvariant(),
            OrganizationId = organizationId,
            OrganizationFhirId = organizationFhirId,
            Role = role,
            SpecialtyHint = string.IsNullOrWhiteSpace(specialtyHint) ? null : specialtyHint.Trim(),
            Status = InvitationStatus.Pending,
            CreatedAt = now,
            CreatedBy = createdBy
        };
    }

    /// <summary>
    /// Asocia el KC user id apenas se crea en KC. Permite revocar/deshabilitar
    /// idempotentemente más tarde aún si el flow falla y la invitation queda
    /// pendiente.
    /// </summary>
    public void AttachKeycloakUser(Guid keycloakUserId)
    {
        if (keycloakUserId == Guid.Empty)
        {
            throw new ArgumentException("KeycloakUserId es obligatorio.", nameof(keycloakUserId));
        }
        if (KeycloakUserId is not null && KeycloakUserId != keycloakUserId)
        {
            throw new InvalidOperationException(
                $"Invitation {Id} ya tiene KeycloakUserId {KeycloakUserId}; no se puede sobreescribir.");
        }
        KeycloakUserId = keycloakUserId;
    }

    public void Accept(Guid keycloakUserId, TimeProvider? timeProvider = null)
    {
        if (Status != InvitationStatus.Pending)
        {
            throw new InvalidOperationException(
                $"No se puede aceptar invitation {Id} en estado {Status}.");
        }
        if (keycloakUserId == Guid.Empty)
        {
            throw new ArgumentException("KeycloakUserId es obligatorio.", nameof(keycloakUserId));
        }

        // Si la invitación ya tenía KC user (caso normal: lo populó CreateInvitationHandler),
        // exigimos coincidencia. Defensa contra completar la invitation con un user distinto
        // al que recibió el email.
        if (KeycloakUserId is not null && KeycloakUserId != keycloakUserId)
        {
            throw new InvalidOperationException(
                $"Invitation {Id} fue emitida para KC user {KeycloakUserId} pero acepta {keycloakUserId}.");
        }

        KeycloakUserId = keycloakUserId;
        Status = InvitationStatus.Accepted;
        AcceptedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Revoke(Guid revokedBy, TimeProvider? timeProvider = null)
    {
        if (Status != InvitationStatus.Pending)
        {
            throw new InvalidOperationException(
                $"No se puede revocar invitation {Id} en estado {Status}.");
        }
        Status = InvitationStatus.Revoked;
        RevokedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        RevokedBy = revokedBy;
    }

    public void MarkExpired(TimeProvider? timeProvider = null)
    {
        if (Status != InvitationStatus.Pending)
        {
            throw new InvalidOperationException(
                $"No se puede expirar invitation {Id} en estado {Status}.");
        }
        Status = InvitationStatus.Expired;
        RevokedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}

/// <summary>
/// Roles asignables a un invitee. Coincide con los realm roles de Keycloak.
/// </summary>
public enum InvitationRole
{
    OrgAdmin = 0,
    Practitioner = 1,
    Auxiliar = 2,
}

public enum InvitationStatus
{
    Pending = 0,
    Accepted = 1,
    Revoked = 2,
    Expired = 3,
}

/// <summary>
/// Mapeo entre <see cref="InvitationRole"/> y los realm roles de Keycloak.
/// Centralizado acá para que handlers/dispatchers/tests apunten al mismo
/// string canonical.
/// </summary>
public static class InvitationRoleNames
{
    public const string OrgAdmin = "org_admin";
    public const string Practitioner = "practitioner";
    public const string Auxiliar = "auxiliar";

    public static string ToRealmRole(InvitationRole role) => role switch
    {
        InvitationRole.OrgAdmin => OrgAdmin,
        InvitationRole.Practitioner => Practitioner,
        InvitationRole.Auxiliar => Auxiliar,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Rol no soportado.")
    };

    public static InvitationRole FromString(string role)
    {
        return role?.Trim().ToLowerInvariant() switch
        {
            OrgAdmin => InvitationRole.OrgAdmin,
            Practitioner => InvitationRole.Practitioner,
            Auxiliar => InvitationRole.Auxiliar,
            _ => throw new ArgumentException(
                $"Role '{role}' no soportado. Valores válidos: {OrgAdmin}, {Practitioner}, {Auxiliar}.",
                nameof(role))
        };
    }

    public static bool TryParseRealmRole(string? role, out InvitationRole parsed)
    {
        switch (role?.Trim().ToLowerInvariant())
        {
            case OrgAdmin: parsed = InvitationRole.OrgAdmin; return true;
            case Practitioner: parsed = InvitationRole.Practitioner; return true;
            case Auxiliar: parsed = InvitationRole.Auxiliar; return true;
            default: parsed = InvitationRole.Practitioner; return false;
        }
    }
}
