namespace IMed.Api.Domain.Locations;

/// <summary>
/// Sedes en las que un usuario puede operar (ADR-008).
///
/// <para>Es una relación N:M deliberada: el técnico que cubre dos sucursales, o el bioanalista que
/// valida lo de toda la red desde el laboratorio central, son el caso normal y no la excepción.</para>
///
/// <para><b>No confundir con permisos.</b> Esta tabla dice <i>dónde</i> puede trabajar alguien; la
/// matriz de permisos dice <i>qué</i> puede hacer. El alcance <c>sede</c> de un permiso se resuelve
/// cruzando ambas: el usuario ve lo operativo de las sedes que tiene asignadas.</para>
/// </summary>
public sealed class UserLocationAssignment
{
    public Guid OrganizationId { get; private set; }
    public Guid UserId { get; private set; }
    public Guid LocationId { get; private set; }

    /// <summary>
    /// Sede que se ofrece primero al entrar. Solo una por usuario y organización; el resto quedan
    /// disponibles en el selector.
    /// </summary>
    public bool IsPrimary { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }
    public Guid? AssignedBy { get; private set; }

    private UserLocationAssignment() { }

    public static UserLocationAssignment Create(
        Guid organizationId,
        Guid userId,
        Guid locationId,
        bool isPrimary = false,
        Guid? assignedBy = null,
        TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId es obligatorio.", nameof(userId));
        }
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("LocationId es obligatorio.", nameof(locationId));
        }

        return new UserLocationAssignment
        {
            OrganizationId = organizationId,
            UserId = userId,
            LocationId = locationId,
            IsPrimary = isPrimary,
            AssignedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            AssignedBy = assignedBy,
        };
    }

    public void MarkPrimary() => IsPrimary = true;

    public void ClearPrimary() => IsPrimary = false;
}
