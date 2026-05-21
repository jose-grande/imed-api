using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Keycloak;

/// <summary>
/// Singleton thread-safe que obtiene tokens del service account
/// (<c>client_credentials</c>) y los cachea hasta ~30s antes de expirar.
///
/// <para>Lo usan tanto <see cref="KeycloakAdminClient"/> (para admin API) como
/// <c>HapiFhirClient</c> (para presentar Bearer al servidor FHIR tras Epica B).</para>
/// </summary>
public sealed class KeycloakServiceAccountTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<KeycloakOptions> options,
    ILogger<KeycloakServiceAccountTokenProvider> logger,
    TimeProvider timeProvider) : IDisposable
{
    public const string TokenHttpClientName = "keycloak-token";

    private readonly KeycloakOptions _options = options.Value;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenExpiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (IsTokenStillValid())
        {
            return _cachedToken!;
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (IsTokenStillValid())
            {
                return _cachedToken!;
            }

            var fresh = await FetchTokenAsync(cancellationToken);
            _cachedToken = fresh.AccessToken;
            _cachedTokenExpiresAt = timeProvider.GetUtcNow().AddSeconds(fresh.ExpiresIn);
            logger.LogDebug("Service account token refrescado, expira a las {ExpiresAt:o}", _cachedTokenExpiresAt);
            return _cachedToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool IsTokenStillValid() =>
        _cachedToken is not null && _cachedTokenExpiresAt > timeProvider.GetUtcNow().AddSeconds(30);

    private async Task<TokenResponse> FetchTokenAsync(CancellationToken cancellationToken)
    {
        var http = httpClientFactory.CreateClient(TokenHttpClientName);
        var tokenUrl = $"{_options.Authority}/protocol/openid-connect/token";

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ServiceAccountClientId,
            ["client_secret"] = _options.ServiceAccountClientSecret
        });

        using var resp = await http.PostAsync(tokenUrl, form, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"No se pudo obtener access_token del service account. Status={(int)resp.StatusCode}, Body={body}");
        }

        return await resp.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Respuesta de token vacia.");
    }

    public void Dispose() => _lock.Dispose();

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("token_type")] string TokenType);
}
