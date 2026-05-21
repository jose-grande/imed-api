using System.Net;
using System.Text.Json;
using WireMock;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// WireMock que simula Keycloak para tests de integración. Cubre:
/// <list type="bullet">
///   <item>OIDC discovery (<c>/.well-known/openid-configuration</c>)</item>
///   <item>JWKS endpoint (<c>/protocol/openid-connect/certs</c>)</item>
///   <item>Token endpoint para client_credentials del service account</item>
///   <item>Admin API: GET/PUT users, roles, role-mappings</item>
/// </list>
///
/// <para>Reset state per-test con <see cref="Reset"/>. Permite scenarios
/// (fail N times then succeed) vía <see cref="EnableFailing"/>.</para>
/// </summary>
public sealed class KeycloakMockServer : IDisposable
{
    private const string Realm = "imed-clinical-dev-test";

    private readonly TestSigningKey _signingKey;
    private readonly WireMockServer _server;
    private readonly Dictionary<Guid, FakeUser> _users = new();

    private int _failingPutAttemptsRemaining;
    private int _failingPostRoleAttemptsRemaining;
    private int _failingExecuteActionsEmailRemaining;
    private int _failingDeleteRoleAttemptsRemaining;

    /// <summary>Captura de la última llamada a <c>execute-actions-email</c> para asserts.</summary>
    public ExecuteActionsEmailCall? LastExecuteActionsEmail { get; private set; }
    public int ExecuteActionsEmailCallCount { get; private set; }
    public int CreateUserCallCount { get; private set; }

    public string BaseUrl => _server.Url!;
    public string Authority => $"{BaseUrl}/realms/{Realm}";

    public KeycloakMockServer(TestSigningKey signingKey)
    {
        _signingKey = signingKey;
        _server = WireMockServer.Start(new WireMock.Settings.WireMockServerSettings
        {
            UseSSL = false,
            StartTimeout = 10_000,
        });

        SetupDiscoveryAndJwks();
        SetupTokenEndpoint();
        SetupAdminApi();
    }

    public string IssueUserToken(
        Guid keycloakUserId,
        string? organizationId = null,
        string? subscriptionTier = null,
        string preferredUsername = "test.user",
        IEnumerable<string>? realmRoles = null,
        TimeSpan? lifetime = null,
        string? email = null,
        string? givenName = null,
        string? familyName = null)
    {
        return _signingKey.IssueAccessToken(
            issuer: Authority,
            keycloakUserId: keycloakUserId,
            organizationId: organizationId,
            subscriptionTier: subscriptionTier,
            preferredUsername: preferredUsername,
            realmRoles: realmRoles,
            lifetime: lifetime,
            email: email,
            givenName: givenName,
            familyName: familyName);
    }

    /// <summary>
    /// Pre-crea un usuario en el "directorio" mockeado, así las llamadas GET
    /// al admin API devuelven datos consistentes.
    /// </summary>
    public void RegisterUser(Guid keycloakUserId, string username = "test.user", string email = "test@imed.test")
    {
        _users[keycloakUserId] = new FakeUser(keycloakUserId, username, email, new(), new()) { Enabled = true };
    }

    public IReadOnlyDictionary<string, string[]> GetUserAttributes(Guid keycloakUserId)
        => _users[keycloakUserId].Attributes;

    public IReadOnlyCollection<string> GetUserRoles(Guid keycloakUserId)
        => _users[keycloakUserId].Roles;

    public bool IsUserEnabled(Guid keycloakUserId)
        => _users.TryGetValue(keycloakUserId, out var u) && u.Enabled;

    public bool TryGetUserByEmail(string email, out Guid keycloakUserId)
    {
        var match = _users.Values.FirstOrDefault(u =>
            string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            keycloakUserId = Guid.Empty;
            return false;
        }
        keycloakUserId = match.Id;
        return true;
    }

    /// <summary>
    /// Hace fallar las próximas <paramref name="count"/> llamadas a
    /// <c>PUT execute-actions-email</c>, después vuelve a comportarse normal.
    /// Útil para probar el outbox de <c>invitation.email-pending</c>.
    /// </summary>
    public void EnableExecuteActionsEmailFailing(int count)
    {
        _failingExecuteActionsEmailRemaining = count;
    }

    /// <summary>
    /// Hace fallar las próximas <paramref name="count"/> llamadas a
    /// <c>DELETE role-mappings/realm</c> (remove role) — para probar el outbox
    /// de <c>team.role-changed</c>.
    /// </summary>
    public void EnableDeleteRoleFailing(int count)
    {
        _failingDeleteRoleAttemptsRemaining = count;
    }

    /// <summary>
    /// Hace fallar las próximas <paramref name="putUserCalls"/> llamadas a
    /// <c>PUT /admin/realms/.../users/{id}</c> y las próximas
    /// <paramref name="postRoleCalls"/> al endpoint de role-mappings, después
    /// vuelve a comportarse normal.
    /// </summary>
    public void EnableFailing(int putUserCalls = 0, int postRoleCalls = 0)
    {
        _failingPutAttemptsRemaining = putUserCalls;
        _failingPostRoleAttemptsRemaining = postRoleCalls;
    }

    public void Reset()
    {
        _users.Clear();
        _failingPutAttemptsRemaining = 0;
        _failingPostRoleAttemptsRemaining = 0;
        _failingExecuteActionsEmailRemaining = 0;
        _failingDeleteRoleAttemptsRemaining = 0;
        LastExecuteActionsEmail = null;
        ExecuteActionsEmailCallCount = 0;
        CreateUserCallCount = 0;
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    // -----------------------------------------------------------------------
    private void SetupDiscoveryAndJwks()
    {
        var realmBase = $"/realms/{Realm}";
        var discovery = new
        {
            issuer = Authority,
            jwks_uri = $"{Authority}/protocol/openid-connect/certs",
            token_endpoint = $"{Authority}/protocol/openid-connect/token",
            authorization_endpoint = $"{Authority}/protocol/openid-connect/auth",
            response_types_supported = new[] { "code" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
        };

        _server
            .Given(Request.Create().WithPath($"{realmBase}/.well-known/openid-configuration").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(discovery)));

        _server
            .Given(Request.Create().WithPath($"{realmBase}/protocol/openid-connect/certs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(_signingKey.ToJwksJson()));
    }

    private void SetupTokenEndpoint()
    {
        _server
            .Given(Request.Create().WithPath($"/realms/{Realm}/protocol/openid-connect/token").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ =>
            {
                var saToken = _signingKey.IssueAccessToken(
                    issuer: Authority,
                    keycloakUserId: Guid.Empty,
                    clientId: "imed-api-sa",
                    preferredUsername: "service-account-imed-api-sa",
                    lifetime: TimeSpan.FromMinutes(5));
                var body = JsonSerializer.Serialize(new
                {
                    access_token = saToken,
                    expires_in = 300,
                    token_type = "Bearer",
                    not_before_policy = 0,
                    scope = "profile email"
                });
                return JsonMessage(HttpStatusCode.OK, body);
            }));
    }

    private void SetupAdminApi()
    {
        var adminUsers = $"/admin/realms/{Realm}/users";

        // POST user (create) — 201 con Location header.
        _server
            .Given(Request.Create()
                .WithPath(adminUsers)
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                CreateUserCallCount++;
                var doc = JsonDocument.Parse(req.Body ?? "{}");
                var id = Guid.NewGuid();
                var email = doc.RootElement.TryGetProperty("email", out var e) ? e.GetString() ?? "" : "";
                var username = doc.RootElement.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
                var attrs = new Dictionary<string, string[]>();
                if (doc.RootElement.TryGetProperty("attributes", out var attrEl) && attrEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var p in attrEl.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.Array)
                        {
                            attrs[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
                        }
                    }
                }
                _users[id] = new FakeUser(id, username, email, attrs, new HashSet<string>()) { Enabled = true };

                return new ResponseMessage
                {
                    StatusCode = (int)HttpStatusCode.Created,
                    Headers = new Dictionary<string, WireMockList<string>>
                    {
                        ["Location"] = new WireMockList<string>($"{BaseUrl}{adminUsers}/{id}"),
                        ["Content-Type"] = new WireMockList<string>("application/json"),
                    },
                };
            }));

        // GET users by email query (?email=X&exact=true).
        _server
            .Given(Request.Create()
                .WithPath(adminUsers)
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var query = req.Query ?? new Dictionary<string, WireMockList<string>>();
                var email = query.TryGetValue("email", out var v) ? v.FirstOrDefault() : null;
                var matches = string.IsNullOrEmpty(email)
                    ? _users.Values.ToArray()
                    : _users.Values.Where(u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)).ToArray();

                var arr = matches.Select(u => new
                {
                    id = u.Id.ToString(),
                    username = u.Username,
                    email = u.Email,
                    enabled = true,
                    emailVerified = false,
                }).ToArray();
                return JsonMessage(HttpStatusCode.OK, arr);
            }));

        // PUT execute-actions-email — KC dispara el email con action token.
        // Sólo capturamos la llamada para asserts; no enviamos email real.
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+/execute-actions-email$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                if (_failingExecuteActionsEmailRemaining > 0)
                {
                    _failingExecuteActionsEmailRemaining--;
                    return JsonMessage(HttpStatusCode.ServiceUnavailable, new { error = "simulated email outage" });
                }
                ExecuteActionsEmailCallCount++;
                var userId = ExtractUserId(req.Path, adminUsers, suffix: "/execute-actions-email");
                var actions = JsonSerializer.Deserialize<string[]>(req.Body ?? "[]") ?? Array.Empty<string>();
                var query = req.Query ?? new Dictionary<string, WireMockList<string>>();
                LastExecuteActionsEmail = new ExecuteActionsEmailCall(
                    UserId: userId,
                    Actions: actions,
                    LifespanSeconds: query.TryGetValue("lifespan", out var ls) && int.TryParse(ls.FirstOrDefault(), out var n) ? n : 0,
                    RedirectUri: query.TryGetValue("redirect_uri", out var ru) ? ru.FirstOrDefault() : null,
                    ClientId: query.TryGetValue("client_id", out var ci) ? ci.FirstOrDefault() : null);
                return new ResponseMessage { StatusCode = (int)HttpStatusCode.NoContent };
            }));

        // GET user
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var userId = ExtractUserId(req.Path, adminUsers);
                if (!_users.TryGetValue(userId, out var u))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "user not found" });
                }
                return JsonMessage(HttpStatusCode.OK, new
                {
                    id = u.Id.ToString(),
                    username = u.Username,
                    email = u.Email,
                    enabled = u.Enabled,
                    attributes = u.Attributes.ToDictionary(kv => kv.Key, kv => (object)kv.Value)
                });
            }));

        // PUT user (merge attributes)
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                if (_failingPutAttemptsRemaining > 0)
                {
                    _failingPutAttemptsRemaining--;
                    return JsonMessage(HttpStatusCode.ServiceUnavailable, new { error = "simulated outage" });
                }

                var userId = ExtractUserId(req.Path, adminUsers);
                if (!_users.TryGetValue(userId, out var u))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "user not found" });
                }

                var doc = JsonDocument.Parse(req.Body ?? "{}");
                if (doc.RootElement.TryGetProperty("enabled", out var enabledEl)
                    && (enabledEl.ValueKind == JsonValueKind.True || enabledEl.ValueKind == JsonValueKind.False))
                {
                    var enabled = enabledEl.GetBoolean();
                    _users[userId] = u with { Enabled = enabled };
                    u = _users[userId];
                }
                if (doc.RootElement.TryGetProperty("attributes", out var attrs)
                    && attrs.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in attrs.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            u.Attributes[prop.Name] = prop.Value.EnumerateArray()
                                .Select(x => x.GetString() ?? "")
                                .ToArray();
                        }
                    }
                }
                return new ResponseMessage { StatusCode = (int)HttpStatusCode.NoContent };
            }));

        // GET role by name
        _server
            .Given(Request.Create()
                .WithPath(new WildcardMatcher($"/admin/realms/{Realm}/roles/*"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var roleName = req.Path.Split('/').Last();
                return JsonMessage(HttpStatusCode.OK, new { id = Guid.NewGuid().ToString(), name = roleName });
            }));

        // GET user role-mappings/realm
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+/role-mappings/realm$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var userId = ExtractUserId(req.Path, adminUsers, suffix: "/role-mappings/realm");
                if (!_users.TryGetValue(userId, out var u))
                {
                    return JsonMessage(HttpStatusCode.OK, Array.Empty<object>());
                }
                return JsonMessage(HttpStatusCode.OK,
                    u.Roles.Select(r => new { id = Guid.NewGuid().ToString(), name = r }).ToArray());
            }));

        // GET /admin/realms/{realm}/roles/{name}/users — usado por F5
        // (lock-out check: contar org_admins de la org).
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^/admin/realms/{Realm}/roles/[^/]+/users$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var roleName = req.Path.Split('/').Reverse().Skip(1).First();
                roleName = Uri.UnescapeDataString(roleName);
                var matches = _users.Values
                    .Where(u => u.Roles.Contains(roleName))
                    .Select(u => new
                    {
                        id = u.Id.ToString(),
                        username = u.Username,
                        email = u.Email,
                        enabled = u.Enabled
                    }).ToArray();
                return JsonMessage(HttpStatusCode.OK, matches);
            }));

        // DELETE role-mappings/realm — remueve role del user.
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+/role-mappings/realm$"))
                .UsingDelete())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                if (_failingDeleteRoleAttemptsRemaining > 0)
                {
                    _failingDeleteRoleAttemptsRemaining--;
                    return JsonMessage(HttpStatusCode.ServiceUnavailable, new { error = "simulated outage" });
                }
                var userId = ExtractUserId(req.Path, adminUsers, suffix: "/role-mappings/realm");
                if (!_users.TryGetValue(userId, out var u))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "user not found" });
                }
                var doc = JsonDocument.Parse(req.Body ?? "[]");
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        var n = nameProp.GetString();
                        if (!string.IsNullOrEmpty(n))
                        {
                            u.Roles.Remove(n);
                        }
                    }
                }
                return new ResponseMessage { StatusCode = (int)HttpStatusCode.NoContent };
            }));

        // POST role-mappings/realm
        _server
            .Given(Request.Create()
                .WithPath(new RegexMatcher($"^{adminUsers}/[a-f0-9-]+/role-mappings/realm$"))
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                if (_failingPostRoleAttemptsRemaining > 0)
                {
                    _failingPostRoleAttemptsRemaining--;
                    return JsonMessage(HttpStatusCode.ServiceUnavailable, new { error = "simulated outage" });
                }
                var userId = ExtractUserId(req.Path, adminUsers, suffix: "/role-mappings/realm");
                if (!_users.TryGetValue(userId, out var u))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "user not found" });
                }

                var doc = JsonDocument.Parse(req.Body ?? "[]");
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var nameProp))
                    {
                        var n = nameProp.GetString();
                        if (!string.IsNullOrEmpty(n))
                        {
                            u.Roles.Add(n);
                        }
                    }
                }
                return new ResponseMessage { StatusCode = (int)HttpStatusCode.NoContent };
            }));
    }

    private static Guid ExtractUserId(string path, string prefix, string suffix = "")
    {
        var idx = path.IndexOf(prefix, StringComparison.Ordinal);
        var tail = path[(idx + prefix.Length + 1)..];
        if (!string.IsNullOrEmpty(suffix) && tail.EndsWith(suffix, StringComparison.Ordinal))
        {
            tail = tail[..^suffix.Length];
        }
        return Guid.Parse(tail);
    }

    private static ResponseMessage JsonMessage(HttpStatusCode status, object body)
    {
        return JsonMessage(status, JsonSerializer.Serialize(body));
    }

    private static ResponseMessage JsonMessage(HttpStatusCode status, string rawJson)
    {
        return new ResponseMessage
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, WireMockList<string>>
            {
                ["Content-Type"] = new WireMockList<string>("application/json")
            },
            BodyData = new BodyData
            {
                BodyAsString = rawJson,
                DetectedBodyType = BodyType.String
            }
        };
    }

    private sealed record FakeUser(
        Guid Id,
        string Username,
        string Email,
        Dictionary<string, string[]> Attributes,
        HashSet<string> Roles)
    {
        public bool Enabled { get; init; } = true;
    }

    public sealed record ExecuteActionsEmailCall(
        Guid UserId,
        IReadOnlyList<string> Actions,
        int LifespanSeconds,
        string? RedirectUri,
        string? ClientId);
}
