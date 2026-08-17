using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Genera una clave RSA por suite de tests para firmar JWTs y exponer su
/// representación pública via JWKS desde el WireMock de Keycloak.
/// </summary>
public sealed class TestSigningKey : IDisposable
{
    public const string KeyId = "test-signing-key-1";

    public RSA Rsa { get; }
    public RsaSecurityKey SecurityKey { get; }
    public SigningCredentials SigningCredentials { get; }

    public TestSigningKey()
    {
        Rsa = RSA.Create(2048);
        SecurityKey = new RsaSecurityKey(Rsa) { KeyId = KeyId };
        SigningCredentials = new SigningCredentials(SecurityKey, SecurityAlgorithms.RsaSha256);
    }

    /// <summary>
    /// Devuelve un JSON Web Key Set (JWKS) con la clave pública. Lo sirve
    /// el WireMock como respuesta a <c>GET /realms/{realm}/protocol/openid-connect/certs</c>.
    /// </summary>
    public string ToJwksJson()
    {
        var parameters = Rsa.ExportParameters(includePrivateParameters: false);
        var n = Base64UrlEncoder.Encode(parameters.Modulus!);
        var e = Base64UrlEncoder.Encode(parameters.Exponent!);

        return $$"""
            {
              "keys": [
                {
                  "kty": "RSA",
                  "use": "sig",
                  "alg": "RS256",
                  "kid": "{{KeyId}}",
                  "n": "{{n}}",
                  "e": "{{e}}"
                }
              ]
            }
            """;
    }

    public string IssueAccessToken(
        string issuer,
        Guid keycloakUserId,
        string? organizationId = null,
        string? subscriptionTier = null,
        string? practitionerId = null,
        string clientId = "imed-clinical-web",
        string preferredUsername = "test.user",
        IEnumerable<string>? realmRoles = null,
        TimeSpan? lifetime = null,
        string? email = null,
        string? givenName = null,
        string? familyName = null)
    {
        var now = DateTime.UtcNow;
        var realmAccess = new
        {
            roles = (realmRoles ?? []).ToArray()
        };

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", keycloakUserId.ToString()),
            new("preferred_username", preferredUsername),
            new("name", preferredUsername),
            new("email", email ?? $"{preferredUsername}@imed.test"),
            new("azp", clientId),
            new("realm_access", System.Text.Json.JsonSerializer.Serialize(realmAccess), "JSON"),
        };
        if (!string.IsNullOrWhiteSpace(givenName))
        {
            claims.Add(new("given_name", givenName));
        }
        if (!string.IsNullOrWhiteSpace(familyName))
        {
            claims.Add(new("family_name", familyName));
        }
        if (!string.IsNullOrWhiteSpace(organizationId))
        {
            claims.Add(new("organization_id", organizationId));
        }
        if (!string.IsNullOrWhiteSpace(subscriptionTier))
        {
            claims.Add(new("subscription_tier", subscriptionTier));
        }
        if (!string.IsNullOrWhiteSpace(practitionerId))
        {
            claims.Add(new("practitioner_id", practitionerId));
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = clientId,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.Add(lifetime ?? TimeSpan.FromMinutes(15)),
            Claims = claims.ToDictionary(c => c.Type, c => (object)c.Value),
            SigningCredentials = SigningCredentials
        };

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(descriptor);
    }

    public void Dispose()
    {
        Rsa.Dispose();
    }
}
