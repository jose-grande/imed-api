namespace IMed.Api.Domain.Subscriptions;

/// <summary>
/// Historial append-only de asignaciones de tier a usuarios. `EffectiveUntil = null`
/// marca la asignación vigente. Se cierra con un UPDATE al asignar un nuevo tier.
/// Es una tabla de auditoría — la fuente operacional del tier vigente es el
/// atributo del usuario en Keycloak (ver ADR-006 rev 4).
/// </summary>
public sealed class UserTierAssignment
{
    public Guid KeycloakUserId { get; private set; }
    public DateTimeOffset AssignedAt { get; private set; }
    public Guid OrganizationId { get; private set; }
    public SubscriptionTier Tier { get; private set; }
    public Guid AssignedBy { get; private set; }
    public DateTimeOffset? EffectiveUntil { get; private set; }

    // EF
    private UserTierAssignment() { }

    public static UserTierAssignment Create(
        Guid keycloakUserId,
        Guid organizationId,
        SubscriptionTier tier,
        Guid assignedBy,
        TimeProvider? timeProvider = null)
    {
        return new UserTierAssignment
        {
            KeycloakUserId = keycloakUserId,
            OrganizationId = organizationId,
            Tier = tier,
            AssignedBy = assignedBy,
            AssignedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            EffectiveUntil = null
        };
    }

    public void Close(DateTimeOffset at)
    {
        if (EffectiveUntil.HasValue)
        {
            throw new InvalidOperationException("Esta asignación ya estaba cerrada.");
        }
        EffectiveUntil = at;
    }
}
