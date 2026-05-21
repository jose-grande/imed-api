namespace IMed.Api.Domain.Subscriptions;

/// <summary>
/// Niveles de suscripción soportados. Per-user (ver ADR-006 rev 4).
/// El orden implícito (Basic &lt; Premium &lt; Platinum) se usa para gating.
/// </summary>
public enum SubscriptionTier
{
    Basic = 0,
    Premium = 1,
    Platinum = 2,
}

public static class SubscriptionTierExtensions
{
    /// <summary>Nombre canónico usado en el claim `subscription_tier` del JWT y como attribute en Keycloak.</summary>
    public static string ToClaimValue(this SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Basic => "BASIC",
        SubscriptionTier.Premium => "PREMIUM",
        SubscriptionTier.Platinum => "PLATINUM",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Tier no soportado.")
    };

    public static SubscriptionTier ParseClaim(string value) => value?.ToUpperInvariant() switch
    {
        "BASIC" => SubscriptionTier.Basic,
        "PREMIUM" => SubscriptionTier.Premium,
        "PLATINUM" => SubscriptionTier.Platinum,
        _ => throw new ArgumentException($"Tier no reconocido: '{value}'", nameof(value))
    };
}
