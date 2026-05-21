using System.Net;
using System.Text.Json;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using WireMock.Util;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// WireMock que simula HAPI FHIR. Cubre las dos operaciones que <c>imed-api</c>
/// hace contra HAPI durante el bootstrap:
/// <list type="bullet">
///   <item><c>POST /fhir/$partition-management-create-partition</c></item>
///   <item><c>POST /fhir/Organization</c> (con header <c>X-Tenant-Partition</c>)</item>
/// </list>
/// </summary>
public sealed class HapiMockServer : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HashSet<string> _createdPartitions = new(StringComparer.Ordinal);
    private readonly List<CreatedOrganization> _organizations = new();
    private readonly Dictionary<string, FakePractitioner> _practitioners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FakePractitionerRole>> _rolesByOrg = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakePractitionerRole> _rolesById = new(StringComparer.Ordinal);
    private int _failingCreatePractitionerRemaining;
    public int CreatePractitionerCallCount { get; private set; }
    public int CreatePractitionerRoleCallCount { get; private set; }

    public string BaseUrl => $"{_server.Url}/fhir";

    public IReadOnlyCollection<string> CreatedPartitions => _createdPartitions;
    public IReadOnlyCollection<CreatedOrganization> Organizations => _organizations;

    public HapiMockServer()
    {
        _server = WireMockServer.Start(new WireMock.Settings.WireMockServerSettings
        {
            UseSSL = false,
            StartTimeout = 10_000,
        });

        SetupPartitionManagement();
        SetupOrganization();
        SetupPractitionerCreate();
        SetupPractitionerRoleCreate();
        SetupPractitionerRoleSearch();
        SetupPractitionerGet();
        SetupPractitionerRoleGet();
    }

    public void Reset()
    {
        _createdPartitions.Clear();
        _organizations.Clear();
        _practitioners.Clear();
        _rolesByOrg.Clear();
        _rolesById.Clear();
        _failingCreatePractitionerRemaining = 0;
        CreatePractitionerCallCount = 0;
        CreatePractitionerRoleCallCount = 0;
    }

    /// <summary>
    /// Hace fallar las próximas <paramref name="count"/> llamadas a
    /// <c>POST /fhir/Practitioner</c> (HAPI down) — útil para probar el outbox
    /// de <c>invitation.lazy-provision-pending</c>.
    /// </summary>
    public void EnableCreatePractitionerFailing(int count)
    {
        _failingCreatePractitionerRemaining = count;
    }

    /// <summary>
    /// Pre-carga un Practitioner + PractitionerRole en el mock para tests
    /// del endpoint <c>GET /api/team</c>.
    /// </summary>
    public void RegisterTeamMember(
        string organizationFhirId,
        string practitionerFhirId,
        string practitionerRoleFhirId,
        string family,
        string given,
        string? specialtyCode = null,
        string? specialtyDisplay = null,
        bool active = true)
    {
        _practitioners[practitionerFhirId] = new FakePractitioner(
            Id: practitionerFhirId,
            Family: family,
            Given: given,
            Active: active);

        if (!_rolesByOrg.TryGetValue(organizationFhirId, out var list))
        {
            list = new List<FakePractitionerRole>();
            _rolesByOrg[organizationFhirId] = list;
        }
        list.Add(new FakePractitionerRole(
            Id: practitionerRoleFhirId,
            PractitionerFhirId: practitionerFhirId,
            SpecialtyCode: specialtyCode,
            SpecialtyDisplay: specialtyDisplay,
            Active: active));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    private void SetupPartitionManagement()
    {
        _server
            .Given(Request.Create()
                .WithPath("/fhir/$partition-management-create-partition")
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var doc = JsonDocument.Parse(req.Body ?? "{}");
                string? name = null;
                if (doc.RootElement.TryGetProperty("parameter", out var paramsArr))
                {
                    foreach (var p in paramsArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("name", out var nm) && nm.GetString() == "name"
                            && p.TryGetProperty("valueCode", out var v))
                        {
                            name = v.GetString();
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(name))
                {
                    return JsonMessage(HttpStatusCode.BadRequest, new { error = "partition name missing" });
                }
                if (!_createdPartitions.Add(name))
                {
                    return JsonMessage(HttpStatusCode.BadRequest, new
                    {
                        issue = new[] { new { diagnostics = "Partition already exists" } }
                    });
                }
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Parameters" });
            }));
    }

    private void SetupOrganization()
    {
        _server
            .Given(Request.Create()
                .WithPath("/fhir/Organization")
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                string? partition = null;
                if (req.Headers != null && req.Headers.TryGetValue("X-Tenant-Partition", out var hv))
                {
                    partition = hv.FirstOrDefault();
                }

                var fhirId = $"org-fhir-{Guid.NewGuid():N}";
                _organizations.Add(new CreatedOrganization(
                    Partition: partition,
                    FhirId: fhirId,
                    BodyJson: req.Body ?? ""));
                return JsonMessage(HttpStatusCode.Created, new
                {
                    resourceType = "Organization",
                    id = fhirId,
                    meta = new { versionId = "1" }
                });
            }));
    }

    private void SetupPractitionerCreate()
    {
        // POST /fhir/Practitioner — emitido por lazy provision (Épica F3) y por
        // upsert manual del perfil profesional (Épica D).
        _server
            .Given(Request.Create()
                .WithPath("/fhir/Practitioner")
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                if (_failingCreatePractitionerRemaining > 0)
                {
                    _failingCreatePractitionerRemaining--;
                    return JsonMessage(HttpStatusCode.ServiceUnavailable, new { error = "simulated HAPI outage" });
                }

                CreatePractitionerCallCount++;
                var doc = JsonDocument.Parse(req.Body ?? "{}");
                var family = string.Empty;
                var given = string.Empty;
                if (doc.RootElement.TryGetProperty("name", out var nameArr)
                    && nameArr.ValueKind == JsonValueKind.Array
                    && nameArr.GetArrayLength() > 0)
                {
                    var n = nameArr[0];
                    family = n.TryGetProperty("family", out var f) ? f.GetString() ?? "" : "";
                    given = n.TryGetProperty("given", out var g)
                        && g.ValueKind == JsonValueKind.Array
                        && g.GetArrayLength() > 0
                        ? g[0].GetString() ?? ""
                        : "";
                }
                var fhirId = $"prac-{Guid.NewGuid():N}";
                _practitioners[fhirId] = new FakePractitioner(fhirId, family, given, Active: true);

                return JsonMessage(HttpStatusCode.Created, new
                {
                    resourceType = "Practitioner",
                    id = fhirId,
                    meta = new { versionId = "1" }
                });
            }));
    }

    private void SetupPractitionerRoleCreate()
    {
        _server
            .Given(Request.Create()
                .WithPath("/fhir/PractitionerRole")
                .UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                CreatePractitionerRoleCallCount++;
                var doc = JsonDocument.Parse(req.Body ?? "{}");
                var practRef = doc.RootElement.TryGetProperty("practitioner", out var pr)
                    && pr.TryGetProperty("reference", out var prRef)
                    ? prRef.GetString() ?? "" : "";
                var orgRef = doc.RootElement.TryGetProperty("organization", out var or)
                    && or.TryGetProperty("reference", out var orRef)
                    ? orRef.GetString() ?? "" : "";

                var practitionerFhirId = practRef.StartsWith("Practitioner/", StringComparison.Ordinal)
                    ? practRef["Practitioner/".Length..] : practRef;
                var orgFhirId = orgRef.StartsWith("Organization/", StringComparison.Ordinal)
                    ? orgRef["Organization/".Length..] : orgRef;

                string? specCode = null;
                string? specDisplay = null;
                if (doc.RootElement.TryGetProperty("specialty", out var specArr)
                    && specArr.ValueKind == JsonValueKind.Array
                    && specArr.GetArrayLength() > 0
                    && specArr[0].TryGetProperty("coding", out var codingArr)
                    && codingArr.ValueKind == JsonValueKind.Array
                    && codingArr.GetArrayLength() > 0)
                {
                    var c = codingArr[0];
                    specCode = c.TryGetProperty("code", out var cc) ? cc.GetString() : null;
                    specDisplay = c.TryGetProperty("display", out var cd) ? cd.GetString() : null;
                }

                var fhirId = $"role-{Guid.NewGuid():N}";
                var role = new FakePractitionerRole(fhirId, practitionerFhirId, specCode, specDisplay, Active: true);
                _rolesById[fhirId] = role;
                if (!_rolesByOrg.TryGetValue(orgFhirId, out var list))
                {
                    list = new List<FakePractitionerRole>();
                    _rolesByOrg[orgFhirId] = list;
                }
                list.Add(role);

                return JsonMessage(HttpStatusCode.Created, new
                {
                    resourceType = "PractitionerRole",
                    id = fhirId,
                    meta = new { versionId = "1" }
                });
            }));
    }

    private void SetupPractitionerRoleSearch()
    {
        // GET /fhir/PractitionerRole?organization=Organization/{id}&...
        // Devuelve un Bundle searchset con los roles registrados para esa org.
        _server
            .Given(Request.Create()
                .WithPath("/fhir/PractitionerRole")
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var orgRef = req.Query?.TryGetValue("organization", out var v) == true
                    ? v.FirstOrDefault()
                    : null;
                var orgFhirId = orgRef?.StartsWith("Organization/", StringComparison.Ordinal) == true
                    ? orgRef["Organization/".Length..]
                    : null;

                var roles = orgFhirId is not null && _rolesByOrg.TryGetValue(orgFhirId, out var list)
                    ? list
                    : new List<FakePractitionerRole>();

                var entries = roles.Select(r => new
                {
                    resource = new
                    {
                        resourceType = "PractitionerRole",
                        id = r.Id,
                        active = r.Active,
                        practitioner = new { reference = $"Practitioner/{r.PractitionerFhirId}" },
                        organization = new { reference = $"Organization/{orgFhirId}" },
                        specialty = r.SpecialtyCode is null ? null : new[]
                        {
                            new { coding = new[] { new { system = "http://snomed.info/sct", code = r.SpecialtyCode, display = r.SpecialtyDisplay } } }
                        }
                    }
                }).ToArray();

                return JsonMessage(HttpStatusCode.OK, new
                {
                    resourceType = "Bundle",
                    type = "searchset",
                    total = entries.Length,
                    entry = entries
                });
            }));
    }

    private void SetupPractitionerGet()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Practitioner/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_practitioners.TryGetValue(id, out var p))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                return JsonMessage(HttpStatusCode.OK, new
                {
                    resourceType = "Practitioner",
                    id = p.Id,
                    active = p.Active,
                    name = new[]
                    {
                        new
                        {
                            family = p.Family,
                            given = new[] { p.Given }
                        }
                    }
                });
            }));
    }

    private void SetupPractitionerRoleGet()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/PractitionerRole/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_rolesById.TryGetValue(id, out var r))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                return JsonMessage(HttpStatusCode.OK, new
                {
                    resourceType = "PractitionerRole",
                    id = r.Id,
                    active = r.Active,
                    practitioner = new { reference = $"Practitioner/{r.PractitionerFhirId}" },
                    specialty = r.SpecialtyCode is null ? null : new[]
                    {
                        new { coding = new[] { new { system = "http://snomed.info/sct", code = r.SpecialtyCode, display = r.SpecialtyDisplay } } }
                    }
                });
            }));
    }

    private static ResponseMessage JsonMessage(HttpStatusCode status, object body)
    {
        return new ResponseMessage
        {
            StatusCode = (int)status,
            Headers = new Dictionary<string, WireMockList<string>>
            {
                ["Content-Type"] = new WireMockList<string>("application/fhir+json")
            },
            BodyData = new BodyData
            {
                BodyAsString = JsonSerializer.Serialize(body),
                DetectedBodyType = BodyType.String
            }
        };
    }

    public sealed record CreatedOrganization(string? Partition, string FhirId, string BodyJson);

    private sealed record FakePractitioner(string Id, string Family, string Given, bool Active);

    private sealed record FakePractitionerRole(
        string Id,
        string PractitionerFhirId,
        string? SpecialtyCode,
        string? SpecialtyDisplay,
        bool Active);
}
