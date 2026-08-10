using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using IMed.Api.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Keycloak;

/// <summary>
/// Adapter contra Keycloak Admin REST API.
/// <para>
/// El access token del service account viene de
/// <see cref="KeycloakServiceAccountTokenProvider"/> (singleton compartido con
/// <c>HapiFhirClient</c>); este cliente solo lo inyecta como Bearer.
/// </para>
/// </summary>
public sealed class KeycloakAdminClient(
    HttpClient httpClient,
    IOptions<KeycloakOptions> options,
    KeycloakServiceAccountTokenProvider tokenProvider,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient
{
    private readonly KeycloakOptions _options = options.Value;

    public async Task SetUserAttributesAsync(
        Guid userId,
        IReadOnlyDictionary<string, string[]> attributes,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var userPath = $"/admin/realms/{_options.Realm}/users/{userId}";

        // Keycloak Admin API no tiene PATCH para attributes -> GET + merge + PUT.
        using var getResponse = await httpClient.GetAsync(userPath, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var existing = await getResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);

        var merged = MergeAttributes(existing, attributes);

        using var putResponse = await httpClient.PutAsJsonAsync(userPath, merged, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            var body = await putResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo PUT user {UserId} en Keycloak. Status={Status}, Body={Body}",
                userId, (int)putResponse.StatusCode, body);
            putResponse.EnsureSuccessStatusCode();
        }

        logger.LogInformation("Atributos de usuario {UserId} actualizados: {Keys}", userId, string.Join(",", attributes.Keys));
    }

    public async Task<Guid> CreateUserAsync(CreateKeycloakUserRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // Body shape de KC POST /admin/realms/{realm}/users:
        // https://www.keycloak.org/docs-api/26.0/rest-api/index.html#UserRepresentation
        var body = new Dictionary<string, object?>
        {
            ["username"] = request.Username,
            ["email"] = request.Email,
            ["enabled"] = request.Enabled,
            ["emailVerified"] = request.EmailVerified,
        };
        if (!string.IsNullOrWhiteSpace(request.FirstName))
        {
            body["firstName"] = request.FirstName;
        }
        if (!string.IsNullOrWhiteSpace(request.LastName))
        {
            body["lastName"] = request.LastName;
        }
        if (request.RequiredActions.Count > 0)
        {
            body["requiredActions"] = request.RequiredActions;
        }
        if (request.Attributes.Count > 0)
        {
            body["attributes"] = request.Attributes;
        }

        var path = $"/admin/realms/{_options.Realm}/users";
        using var response = await httpClient.PostAsJsonAsync(path, body, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo CreateUser en Keycloak. Status={Status}, Body={Body}",
                (int)response.StatusCode, errorBody);
            response.EnsureSuccessStatusCode();
        }

        // KC devuelve 201 con header Location: /admin/realms/{realm}/users/{id}.
        // El id está al final del path.
        var location = response.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("KC no devolvió Location header tras CreateUser.");
        var lastSegment = location[(location.LastIndexOf('/') + 1)..];
        if (!Guid.TryParse(lastSegment, out var userId))
        {
            throw new InvalidOperationException($"KC devolvió Location no parseable: {location}");
        }

        logger.LogInformation("Usuario {UserId} creado en Keycloak (email={Email})", userId, request.Email);
        return userId;
    }

    public async Task<KeycloakUserSnapshot?> GetUserByEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // KC search by email: GET /admin/realms/{realm}/users?email={email}&exact=true
        // Devuelve un array; si está vacío → null. Si tiene más de uno (caso
        // patológico — KC no enforza unique email a menos que se configure
        // realm.duplicateEmailsAllowed=false), devolvemos el primero.
        var path = $"/admin/realms/{_options.Realm}/users?email={Uri.EscapeDataString(email)}&exact=true";
        using var response = await httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        var users = await response.Content.ReadFromJsonAsync<KeycloakUserDto[]>(cancellationToken)
            ?? Array.Empty<KeycloakUserDto>();
        if (users.Length == 0)
        {
            return null;
        }

        var first = users[0];
        return new KeycloakUserSnapshot
        {
            Id = first.Id,
            Email = first.Email ?? email,
            Username = first.Username,
            Enabled = first.Enabled,
            EmailVerified = first.EmailVerified,
        };
    }

    public async Task ExecuteActionsEmailAsync(
        Guid userId,
        IReadOnlyList<string> actions,
        int lifespanSeconds,
        string? redirectUri,
        string? clientId,
        CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // KC: PUT /admin/realms/{realm}/users/{id}/execute-actions-email
        // Query params: lifespan=<seconds>, redirect_uri=<>, client_id=<>
        // Body: array de actions ["UPDATE_PASSWORD", "VERIFY_EMAIL", ...]
        var query = $"?lifespan={lifespanSeconds}";
        if (!string.IsNullOrWhiteSpace(redirectUri))
        {
            query += $"&redirect_uri={Uri.EscapeDataString(redirectUri)}";
        }
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            query += $"&client_id={Uri.EscapeDataString(clientId)}";
        }
        var path = $"/admin/realms/{_options.Realm}/users/{userId}/execute-actions-email{query}";

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = JsonContent.Create(actions),
        };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo execute-actions-email para user {UserId}. Status={Status}, Body={Body}",
                userId, (int)response.StatusCode, body);
            response.EnsureSuccessStatusCode();
        }

        logger.LogInformation(
            "execute-actions-email disparado para user {UserId} con actions {Actions}",
            userId, string.Join(",", actions));
    }

    public async Task<IReadOnlyList<string>> GetUserRealmRolesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var path = $"/admin/realms/{_options.Realm}/users/{userId}/role-mappings/realm";
        using var resp = await httpClient.GetAsync(path, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Array.Empty<string>();
        }
        resp.EnsureSuccessStatusCode();
        var roles = await resp.Content.ReadFromJsonAsync<KeycloakRole[]>(cancellationToken)
            ?? Array.Empty<KeycloakRole>();
        return roles.Select(r => r.Name).Where(n => !string.IsNullOrEmpty(n)).ToArray();
    }

    public async Task<KeycloakUserSnapshot?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var path = $"/admin/realms/{_options.Realm}/users/{userId}";
        using var resp = await httpClient.GetAsync(path, cancellationToken);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<KeycloakUserDto>(cancellationToken);
        if (dto is null || dto.Id == Guid.Empty)
        {
            return null;
        }
        return new KeycloakUserSnapshot
        {
            Id = dto.Id,
            Email = dto.Email ?? string.Empty,
            Username = dto.Username,
            Enabled = dto.Enabled,
            EmailVerified = dto.EmailVerified
        };
    }

    public async Task SetUserEnabledAsync(Guid userId, bool enabled, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // Mismo patrón que SetUserAttributesAsync: GET + merge + PUT, conservando
        // attributes/email/username/etc. Si solo PUT-eamos `{enabled: false}` KC
        // borra todo el resto.
        var userPath = $"/admin/realms/{_options.Realm}/users/{userId}";
        using var getResponse = await httpClient.GetAsync(userPath, cancellationToken);
        getResponse.EnsureSuccessStatusCode();

        var existing = await getResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        // Reutilizamos MergeAttributes (sin updates) para deduplicar los valores
        // de atributos single-valued; un PUT con duplicados sería rechazado por
        // Declarative User Profile con 400 incluso en un simple toggle de enabled.
        var merged = MergeAttributes(existing, new Dictionary<string, string[]>());
        merged["enabled"] = enabled;

        using var putResponse = await httpClient.PutAsJsonAsync(userPath, merged, cancellationToken);
        if (!putResponse.IsSuccessStatusCode)
        {
            var body = await putResponse.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo PUT user {UserId} en SetUserEnabled. Status={Status}, Body={Body}",
                userId, (int)putResponse.StatusCode, body);
            putResponse.EnsureSuccessStatusCode();
        }

        logger.LogInformation("User {UserId} enabled={Enabled} en Keycloak.", userId, enabled);
    }

    public async Task AssignRealmRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // Idempotente: si el rol ya está asignado, NO POST-eamos de nuevo.
        // Crítico para el worker del outbox que retira sobre fallas — un retry
        // tras un POST exitoso parcialmente no debe romper.
        var existingPath = $"/admin/realms/{_options.Realm}/users/{userId}/role-mappings/realm";
        using var existingResp = await httpClient.GetAsync(existingPath, cancellationToken);
        existingResp.EnsureSuccessStatusCode();
        var existing = await existingResp.Content.ReadFromJsonAsync<KeycloakRole[]>(cancellationToken)
            ?? [];
        if (existing.Any(r => string.Equals(r.Name, roleName, StringComparison.Ordinal)))
        {
            logger.LogDebug("Rol {Role} ya asignado al usuario {UserId} — no-op.", roleName, userId);
            return;
        }

        var roleLookupPath = $"/admin/realms/{_options.Realm}/roles/{Uri.EscapeDataString(roleName)}";
        using var roleResp = await httpClient.GetAsync(roleLookupPath, cancellationToken);
        roleResp.EnsureSuccessStatusCode();
        var role = await roleResp.Content.ReadFromJsonAsync<KeycloakRole>(cancellationToken)
            ?? throw new InvalidOperationException($"Rol '{roleName}' no encontrado en el realm.");

        using var assignResp = await httpClient.PostAsJsonAsync(existingPath, new[] { role }, cancellationToken);
        if (!assignResp.IsSuccessStatusCode)
        {
            var body = await assignResp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo asignacion de rol {Role} al usuario {UserId}. Status={Status}, Body={Body}",
                roleName, userId, (int)assignResp.StatusCode, body);
            assignResp.EnsureSuccessStatusCode();
        }

        logger.LogInformation("Rol {Role} asignado al usuario {UserId}", roleName, userId);
    }

    public async Task RemoveRealmRoleAsync(Guid userId, string roleName, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        // Idempotente: chequear si el role está asignado antes de DELETE.
        var existingPath = $"/admin/realms/{_options.Realm}/users/{userId}/role-mappings/realm";
        using var existingResp = await httpClient.GetAsync(existingPath, cancellationToken);
        existingResp.EnsureSuccessStatusCode();
        var existing = await existingResp.Content.ReadFromJsonAsync<KeycloakRole[]>(cancellationToken)
            ?? [];
        var match = existing.FirstOrDefault(r => string.Equals(r.Name, roleName, StringComparison.Ordinal));
        if (match is null)
        {
            logger.LogDebug("Rol {Role} no estaba asignado al user {UserId}; no-op.", roleName, userId);
            return;
        }

        // KC requiere body con array de role representations en DELETE — usamos
        // SendAsync con HttpMethod.Delete + JsonContent ya que HttpClient.DeleteAsync
        // no acepta body directamente.
        using var req = new HttpRequestMessage(HttpMethod.Delete, existingPath)
        {
            Content = JsonContent.Create(new[] { match })
        };
        using var resp = await httpClient.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Fallo DELETE role mapping {Role} de user {UserId}. Status={Status}, Body={Body}",
                roleName, userId, (int)resp.StatusCode, body);
            resp.EnsureSuccessStatusCode();
        }
        logger.LogInformation("Rol {Role} removido del user {UserId}.", roleName, userId);
    }

    public async Task<IReadOnlyList<Guid>> GetUsersWithRealmRoleAsync(string roleName, CancellationToken cancellationToken = default)
    {
        await EnsureBearerAsync(cancellationToken);

        var path = $"/admin/realms/{_options.Realm}/roles/{Uri.EscapeDataString(roleName)}/users";
        using var resp = await httpClient.GetAsync(path, cancellationToken);
        resp.EnsureSuccessStatusCode();

        var users = await resp.Content.ReadFromJsonAsync<KeycloakUserDto[]>(cancellationToken)
            ?? Array.Empty<KeycloakUserDto>();
        return users.Select(u => u.Id).Where(id => id != Guid.Empty).ToArray();
    }

    private async Task EnsureBearerAsync(CancellationToken cancellationToken)
    {
        var token = await tokenProvider.GetAccessTokenAsync(cancellationToken);
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static Dictionary<string, object?> MergeAttributes(JsonElement existingUser, IReadOnlyDictionary<string, string[]> updates)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var prop in existingUser.EnumerateObject())
        {
            if (prop.NameEquals("attributes"))
            {
                continue;
            }
            dict[prop.Name] = prop.Value.Clone();
        }

        var mergedAttrs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (existingUser.TryGetProperty("attributes", out var existingAttrs)
            && existingAttrs.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in existingAttrs.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    // De-dup valores: con Declarative User Profile, los atributos
                    // single-valued (organization_id / practitioner_id /
                    // subscription_tier) rechazan el PUT con 400
                    // "error-invalid-multivalued-size" si reenviamos valores
                    // repetidos. KC puede devolver duplicados en el GET (quirk de
                    // storage); al deduplicar antes del PUT la escritura se
                    // auto-sana en vez de propagar la corrupción.
                    mergedAttrs[prop.Name] = prop.Value.EnumerateArray()
                        .Select(x => x.GetString() ?? string.Empty)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                }
            }
        }

        foreach (var (k, v) in updates)
        {
            if (v.Length == 0)
            {
                mergedAttrs.Remove(k);
            }
            else
            {
                mergedAttrs[k] = v.Distinct(StringComparer.Ordinal).ToArray();
            }
        }

        dict["attributes"] = mergedAttrs;
        return dict;
    }

    private sealed record KeycloakRole(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name);

    private sealed record KeycloakUserDto
    {
        [JsonPropertyName("id")] public Guid Id { get; init; }
        [JsonPropertyName("email")] public string? Email { get; init; }
        [JsonPropertyName("username")] public string? Username { get; init; }
        [JsonPropertyName("enabled")] public bool Enabled { get; init; }
        [JsonPropertyName("emailVerified")] public bool EmailVerified { get; init; }
    }
}
