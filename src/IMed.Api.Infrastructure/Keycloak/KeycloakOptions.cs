namespace IMed.Api.Infrastructure.Keycloak;

/// <summary>
/// Options bound desde <c>Keycloak</c> en appsettings.
/// </summary>
public sealed class KeycloakOptions
{
    public const string SectionName = "Keycloak";

    /// <summary>Issuer del realm, ej: <c>http://localhost:8090/realms/imed-clinical-dev</c>.</summary>
    public string Authority { get; init; } = string.Empty;

    /// <summary>URL base de la Keycloak (sin path de realm), ej: <c>http://localhost:8090</c>.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Nombre del realm, ej: <c>imed-clinical-dev</c>.</summary>
    public string Realm { get; init; } = string.Empty;

    /// <summary>ClientId del service account confidential (ej: <c>imed-api-sa</c>).</summary>
    public string ServiceAccountClientId { get; init; } = string.Empty;

    /// <summary>Secret del service account. En staging/prod viene de vault.</summary>
    public string ServiceAccountClientSecret { get; init; } = string.Empty;

    public bool RequireHttpsMetadata { get; init; } = true;
}
