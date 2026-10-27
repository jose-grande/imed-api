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
public sealed partial class HapiMockServer : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HashSet<string> _createdPartitions = new(StringComparer.Ordinal);
    private readonly List<CreatedOrganization> _organizations = new();
    private readonly Dictionary<string, FakePractitioner> _practitioners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FakePractitionerRole>> _rolesByOrg = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakePractitionerRole> _rolesById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeServiceRequest> _serviceRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _encounterStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeObservation> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FakeDiagnosticReport> _diagnosticReports = new(StringComparer.Ordinal);
    private readonly List<CreatedProvenance> _provenances = new();
    private int _failingCreatePractitionerRemaining;
    public int CreatePractitionerCallCount { get; private set; }
    public int CreatePractitionerRoleCallCount { get; private set; }

    public string BaseUrl => $"{_server.Url}/fhir";

    /// <summary>
    /// Si la última petición recibida cuyo path contiene <paramref name="pathFragment"/> pidió
    /// saltarse la caché de búsquedas.
    ///
    /// <para>Existe para una sola prueba, pero de las que valen: HAPI reutiliza el resultado de una
    /// búsqueda idéntica durante 60 segundos, y sin este header el gateway lee datos viejos sin que
    /// nada falle. Es un modo de falla silencioso, así que se verifica sobre el request real que
    /// sale del proxy y no sobre la intención del código.</para>
    /// </summary>
    public bool LastRequestHadNoCache(string pathFragment)
    {
        var entry = _server.LogEntries
            .LastOrDefault(e => e.RequestMessage.Path.Contains(pathFragment, StringComparison.OrdinalIgnoreCase));

        return entry?.RequestMessage.Headers is { } headers
            && headers.TryGetValue("Cache-Control", out var values)
            && values.Any(v => v.Contains("no-cache", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyCollection<string> CreatedPartitions => _createdPartitions;
    public IReadOnlyCollection<CreatedOrganization> Organizations => _organizations;
    public IReadOnlyCollection<CreatedProvenance> Provenances => _provenances;

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
        SetupExternalPractitionerSearch();
        SetupServiceRequestCreate();
        SetupServiceRequestGet();
        SetupServiceRequestSearch();
        SetupServiceRequestUpdate();
        SetupEncounterGet();
        SetupSpecimenCreate();
        SetupProvenanceCreate();
        SetupPatientGet();
        SetupObservation();
        SetupDiagnosticReport();
        SetupDocuments();
    }

    public void Reset()
    {
        _createdPartitions.Clear();
        _organizations.Clear();
        _practitioners.Clear();
        _rolesByOrg.Clear();
        _rolesById.Clear();
        _serviceRequests.Clear();
        _encounterStates.Clear();
        _observations.Clear();
        _diagnosticReports.Clear();
        _provenances.Clear();
        ResetDocuments();
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
                var role = new FakePractitionerRole(
                    fhirId, practitionerFhirId, specCode, specDisplay, Active: true, OrganizationFhirId: orgFhirId);
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
                    organization = r.OrganizationFhirId is null ? null : new { reference = $"Organization/{r.OrganizationFhirId}" },
                    location = r.Locations?.Select(l => new { reference = $"Location/{l}" }).ToArray(),
                    specialty = r.SpecialtyCode is null ? null : new[]
                    {
                        new { coding = new[] { new { system = "http://snomed.info/sct", code = r.SpecialtyCode, display = r.SpecialtyDisplay } } }
                    }
                });
            }));

        // PUT: reemplaza el rol como HAPI, incluidas sus sedes (FAC-9). Una sede que no existe en
        // el almacén se rechaza igual que HAPI rechaza una referencia rota.
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/PractitionerRole/[a-zA-Z0-9-]+$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_rolesById.TryGetValue(id, out var previo))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                using var doc = JsonDocument.Parse(req.Body ?? "{}");
                var locations = doc.RootElement.TryGetProperty("location", out var locs) && locs.ValueKind == JsonValueKind.Array
                    ? locs.EnumerateArray()
                        .Select(l => l.GetProperty("reference").GetString()!["Location/".Length..])
                        .ToList()
                    : new List<string>();
                var faltante = locations.FirstOrDefault(l => GetResource("Location", l) is null);
                if (faltante is not null)
                {
                    return JsonMessage(HttpStatusCode.UnprocessableEntity, new { error = $"HAPI-1094: Location/{faltante} not found" });
                }
                _rolesById[id] = previo with { Locations = locations };
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "PractitionerRole", id, meta = new { versionId = "2" } });
            }));
    }

    /// <summary>Sedes del PractitionerRole, en el orden en que se escribieron (FAC-9).</summary>
    public IReadOnlyList<string> PractitionerRoleLocations(string roleId) =>
        _rolesById.TryGetValue(roleId, out var r) ? r.Locations ?? [] : [];

    // ===================== LIS-3: recepción de muestras =====================

    private void SetupExternalPractitionerSearch()
    {
        // GET /fhir/Practitioner?identifier=... (dedupe de médico externo).
        // Devolvemos vacío → el handler crea un Practitioner nuevo.
        _server
            .Given(Request.Create().WithPath("/fhir/Practitioner").UsingGet())
            .RespondWith(Response.Create().WithCallback(_ =>
                JsonMessage(HttpStatusCode.OK, new { resourceType = "Bundle", type = "searchset", total = 0, entry = Array.Empty<object>() })));
    }

    private void SetupServiceRequestCreate()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/ServiceRequest").UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var root = JsonDocument.Parse(req.Body ?? "{}").RootElement;
                var patientId = ParseReference(root, "subject", "Patient/");
                string? loinc = null;
                string? localCode = null;
                string? localSystem = null;
                string? codeText = null;
                if (root.TryGetProperty("code", out var code) && code.TryGetProperty("concept", out var concept))
                {
                    // Se guarda cada coding con su sistema, como HAPI. Quedarse con `coding[0]` y
                    // republicarlo como LOINC era justo el error que se quería poder detectar.
                    if (concept.TryGetProperty("coding", out var coding)
                        && coding.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var c in coding.EnumerateArray())
                        {
                            var sys = c.TryGetProperty("system", out var sEl) ? sEl.GetString() : null;
                            var val = c.TryGetProperty("code", out var cc) ? cc.GetString() : null;
                            if (string.IsNullOrWhiteSpace(val)) { continue; }
                            if (string.Equals(sys, "http://loinc.org", StringComparison.OrdinalIgnoreCase))
                            {
                                loinc = val;
                            }
                            else
                            {
                                localCode ??= val;
                                localSystem ??= sys;
                            }
                        }
                    }
                    if (concept.TryGetProperty("text", out var t))
                    {
                        codeText = t.GetString();
                    }
                }
                var priority = root.TryGetProperty("priority", out var pr) ? pr.GetString() : null;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "active" : "active";
                var intent = root.TryGetProperty("intent", out var it) ? it.GetString() ?? "order" : "order";
                var encounter = ParseReference(root, "encounter", "Encounter/");
                string? requisition = root.TryGetProperty("requisition", out var rq) && rq.TryGetProperty("value", out var rqv)
                    ? rqv.GetString() : null;

                var id = $"sr-{Guid.NewGuid():N}";
                var authoredOn = root.TryGetProperty("authoredOn", out var ao) ? ao.GetString() : null;
                _serviceRequests[id] = new FakeServiceRequest(
                    id, patientId, loinc, codeText, priority, status, intent, encounter, requisition,
                    localCode, localSystem, authoredOn);
                return JsonMessage(HttpStatusCode.Created, new { resourceType = "ServiceRequest", id, meta = new { versionId = "1" } });
            }));
    }

    private void SetupServiceRequestGet()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/ServiceRequest/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_serviceRequests.TryGetValue(id, out var sr))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                return JsonMessage(HttpStatusCode.OK, new
                {
                    resourceType = "ServiceRequest",
                    id = sr.Id,
                    status = sr.Status,
                    intent = sr.Intent,
                    subject = new { reference = $"Patient/{sr.PatientFhirId}" },
                    encounter = sr.Encounter is null ? null : new { reference = $"Encounter/{sr.Encounter}" },
                    requisition = sr.Requisition is null ? null : new { system = "http://imed.local/requisition", value = sr.Requisition },
                    priority = sr.Priority,
                    authoredOn = sr.AuthoredOn,
                    code = new
                    {
                        concept = new
                        {
                            coding = CodingsOf(sr),
                            text = sr.CodeText
                        }
                    }
                });
            }));
    }

    /// <summary>Búsqueda de ServiceRequest por encounter/status (recepción por requisición + colas, R1/R3/R5).</summary>
    /// <summary>
    /// PUT de una orden: reemplaza la fila conservando el id, como haría HAPI al versionar.
    /// Es lo que usa la promoción <c>draft → active</c>, del cliente o de la reconciliación.
    /// </summary>
    private void SetupServiceRequestUpdate()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/ServiceRequest/[a-zA-Z0-9-]+$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_serviceRequests.TryGetValue(id, out var previo))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }

                var root = JsonDocument.Parse(req.Body ?? "{}").RootElement;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? previo.Status : previo.Status;
                var (loinc, localCode, localSystem, codeText) = ParseLabCoding(root);

                _serviceRequests[id] = previo with
                {
                    Status = status,
                    Loinc = loinc,
                    LocalCode = localCode,
                    LocalSystem = localSystem,
                    CodeText = codeText ?? previo.CodeText,
                    // Se guarda lo que venga en el cuerpo, como HAPI. El gateway reescribe
                    // `authoredOn` en cada PUT, así que promover una orden le cambia la fecha de
                    // emisión — está anotado como pendiente, y el mock tiene que reflejarlo para
                    // que se pueda ver cuando se arregle.
                    AuthoredOn = root.TryGetProperty("authoredOn", out var ao) ? ao.GetString() : previo.AuthoredOn,
                };
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "ServiceRequest", id, meta = new { versionId = "2" } });
            }));
    }

    /// <summary>
    /// <c>GET /fhir/Encounter/{id}</c>. Devuelve solo el estado, que es lo único que la
    /// reconciliación necesita para saber si la consulta ya terminó.
    /// </summary>
    private void SetupEncounterGet()
    {
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Encounter/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (_encounterStates.TryGetValue(id, out var status))
                {
                    return JsonMessage(HttpStatusCode.OK, new { resourceType = "Encounter", id, status });
                }
                return ReadDocument("Encounter", id);
            }));
    }

    /// <summary>Declara el estado de un Encounter para las pruebas que dependen de él.</summary>
    public void RegisterEncounter(string encounterFhirId, string status) =>
        _encounterStates[encounterFhirId] = status;

    /// <summary>Extrae el coding de una orden repartido por sistema, como lo guarda HAPI.</summary>
    private static (string? Loinc, string? LocalCode, string? LocalSystem, string? CodeText) ParseLabCoding(
        JsonElement root)
    {
        string? loinc = null, localCode = null, localSystem = null, codeText = null;
        if (!root.TryGetProperty("code", out var code) || !code.TryGetProperty("concept", out var concept))
        {
            return (loinc, localCode, localSystem, codeText);
        }

        if (concept.TryGetProperty("coding", out var coding) && coding.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in coding.EnumerateArray())
            {
                var sys = c.TryGetProperty("system", out var sEl) ? sEl.GetString() : null;
                var val = c.TryGetProperty("code", out var cc) ? cc.GetString() : null;
                if (string.IsNullOrWhiteSpace(val)) { continue; }
                if (string.Equals(sys, "http://loinc.org", StringComparison.OrdinalIgnoreCase))
                {
                    loinc = val;
                }
                else
                {
                    localCode ??= val;
                    localSystem ??= sys;
                }
            }
        }
        if (concept.TryGetProperty("text", out var t))
        {
            codeText = t.GetString();
        }
        return (loinc, localCode, localSystem, codeText);
    }

    /// <summary>
    /// Devuelve el coding tal como se guardó: LOINC si el examen lo tiene, más el código propio
    /// con su sistema. Es lo que hace HAPI, y lo que permite que una prueba note si el gateway
    /// republica un código local bajo <c>http://loinc.org</c>.
    /// </summary>
    private static object[]? CodingsOf(FakeServiceRequest sr)
    {
        var codings = new List<object>();
        if (sr.Loinc is not null)
        {
            codings.Add(new { system = "http://loinc.org", code = sr.Loinc, display = sr.CodeText });
        }
        if (sr.LocalCode is not null)
        {
            codings.Add(new { system = sr.LocalSystem ?? "http://imed.local/panel", code = sr.LocalCode, display = sr.CodeText });
        }
        return codings.Count == 0 ? null : codings.ToArray();
    }

    private void SetupServiceRequestSearch()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/ServiceRequest").UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                string? encParam = req.Query != null && req.Query.TryGetValue("encounter", out var ev) ? ev.FirstOrDefault() : null;
                string? statusParam = req.Query != null && req.Query.TryGetValue("status", out var sv) ? sv.FirstOrDefault() : null;
                var encId = encParam?.Replace("Encounter/", string.Empty, StringComparison.Ordinal);

                var matches = _serviceRequests.Values.Where(sr =>
                    (encId is null || sr.Encounter == encId)
                    && (statusParam is null || statusParam.Split(',').Contains(sr.Status))).ToList();

                var entries = matches.Select(sr => new
                {
                    resource = new
                    {
                        resourceType = "ServiceRequest",
                        id = sr.Id,
                        status = sr.Status,
                        intent = sr.Intent,
                        subject = new { reference = $"Patient/{sr.PatientFhirId}" },
                        encounter = sr.Encounter is null ? null : new { reference = $"Encounter/{sr.Encounter}" },
                        // La búsqueda omitía `requisition` (el GET individual sí lo devuelve), así
                        // que toda orden hallada por búsqueda llegaba sin agrupación y los flujos
                        // que filtran por requisición —cola de pendientes, resolve— la descartaban.
                        requisition = sr.Requisition is null ? null : new { system = "http://imed.local/requisition", value = sr.Requisition },
                        priority = sr.Priority,
                        authoredOn = sr.AuthoredOn,
                        code = new
                        {
                            concept = new
                            {
                                coding = CodingsOf(sr),
                                text = sr.CodeText
                            }
                        }
                    }
                }).ToArray();

                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Bundle", type = "searchset", total = entries.Length, entry = entries });
            }));
    }

    /// <summary>Siembra una orden directamente (helper de tests para flujos de recepción por requisición).</summary>
    /// <summary>Cambia el estado de una orden sembrada (emitir, revocar…).</summary>
    public void SetServiceRequestStatus(string id, string status) =>
        _serviceRequests[id] = _serviceRequests[id] with { Status = status };

    /// <summary>
    /// Siembra una orden. <paramref name="localCode"/> permite sembrarla como la escribe el cliente
    /// real cuando el examen es un panel propio de la clínica: **solo** código local, sin LOINC.
    /// Sembrar el código del panel en <paramref name="loinc"/> —que es lo único que se podía hacer
    /// antes— produce un recurso que ninguna orden real tiene, y hace pasar los caminos que resuelven
    /// el catálogo por el campo equivocado.
    /// </summary>
    public string SeedServiceRequest(
        string patientFhirId, string? loinc, string? codeText, string encounter, string requisition,
        string status = "active", string? localCode = null, string? localSystem = null)
    {
        var id = $"sr-{Guid.NewGuid():N}";
        _serviceRequests[id] = new FakeServiceRequest(
            id, patientFhirId, loinc, codeText, "routine", status, "order", encounter, requisition,
            LocalCode: localCode, LocalSystem: localSystem);
        return id;
    }

    private void SetupSpecimenCreate()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/Specimen").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ =>
                JsonMessage(HttpStatusCode.Created, new { resourceType = "Specimen", id = $"spec-{Guid.NewGuid():N}", meta = new { versionId = "1" } })));
    }

    private void SetupProvenanceCreate()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/Provenance").UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                // Se registra lo creado para poder afirmar sobre la cadena de custodia: sin esto
                // un camino que deja de escribir Provenance pasa los tests igual.
                var id = $"prov-{Guid.NewGuid():N}";
                var root = JsonDocument.Parse(req.Body ?? "{}").RootElement;
                _provenances.Add(new CreatedProvenance(
                    id,
                    ParseFirstArrayReference(root, "target"),
                    root.TryGetProperty("activity", out var act)
                        && act.TryGetProperty("coding", out var codings)
                        && codings.ValueKind == JsonValueKind.Array
                        && codings.GetArrayLength() > 0
                        && codings[0].TryGetProperty("code", out var code)
                            ? code.GetString()
                            : null,
                    ParseAgentWho(root)));
                return JsonMessage(HttpStatusCode.Created, new { resourceType = "Provenance", id, meta = new { versionId = "1" } });
            }));
    }

    /// <summary>Primera referencia de un arreglo de referencias (`target` de Provenance).</summary>
    private static string ParseFirstArrayReference(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var arr)
            && arr.ValueKind == JsonValueKind.Array
            && arr.GetArrayLength() > 0
            && arr[0].TryGetProperty("reference", out var r))
        {
            return r.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Referencia del primer agente (`agent[0].who.reference`).</summary>
    private static string? ParseAgentWho(JsonElement root)
    {
        if (root.TryGetProperty("agent", out var agents)
            && agents.ValueKind == JsonValueKind.Array
            && agents.GetArrayLength() > 0
            && agents[0].TryGetProperty("who", out var who)
            && who.TryGetProperty("reference", out var r))
        {
            return r.GetString();
        }
        return null;
    }

    private static string ParseReference(JsonElement root, string property, string prefix)
    {
        if (root.TryGetProperty(property, out var el)
            && el.TryGetProperty("reference", out var r)
            && r.ValueKind == JsonValueKind.String)
        {
            var s = r.GetString() ?? string.Empty;
            return s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : s;
        }
        return string.Empty;
    }

    // ===================== LIS-4: resultados =====================

    private void SetupPatientGet()
    {
        // GET /fhir/Patient/{id} — demografía para el flag (genérico).
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Patient/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                return JsonMessage(HttpStatusCode.OK, new
                {
                    resourceType = "Patient",
                    id,
                    gender = "male",
                    birthDate = "1985-01-01"
                });
            }));
    }

    private void SetupObservation()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/Observation").UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = $"obs-{Guid.NewGuid():N}";
                _observations[id] = ParseObservation(id, req.Body);
                return JsonMessage(HttpStatusCode.Created, new { resourceType = "Observation", id, meta = new { versionId = "1" } });
            }));

        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Observation/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_observations.TryGetValue(id, out var o))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                return JsonMessage(HttpStatusCode.OK, ObservationToFhir(o));
            }));

        // PUT: corrección de resultado (LIS-4b). HAPI versiona el recurso; el mock reemplaza
        // la fila conservando el id, que es lo que el flujo necesita para releer el valor nuevo.
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Observation/[a-zA-Z0-9-]+$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_observations.ContainsKey(id))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                _observations[id] = ParseObservation(id, req.Body);
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Observation", id, meta = new { versionId = "2" } });
            }));

        // GET /fhir/Observation?subject=Patient/x&code=loinc|... — delta check (LIS-5).
        _server
            .Given(Request.Create().WithPath("/fhir/Observation").UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                // Por encuentro (preclínica, Épica P): se sirven del almacén genérico, que es donde
                // escriben las transacciones.
                if (req.Query?.ContainsKey("encounter") == true)
                {
                    return SearchDocuments("Observation", req);
                }
                var subj = req.Query?.TryGetValue("subject", out var sv) == true ? sv.FirstOrDefault() : null;
                var patient = subj?.StartsWith("Patient/", StringComparison.Ordinal) == true ? subj["Patient/".Length..] : subj;
                var codeParam = req.Query?.TryGetValue("code", out var cv) == true ? cv.FirstOrDefault() : null;
                var loinc = codeParam?.Contains('|') == true ? codeParam[(codeParam.IndexOf('|') + 1)..] : codeParam;

                var matches = _observations.Values
                    .Where(o => (patient is null || o.PatientFhirId == patient)
                        && (loinc is null || o.Loinc == loinc)
                        && o.ValueNumeric is not null)
                    .Reverse() // más recientes primero (orden de inserción)
                    .Select(o => new { resource = ObservationToFhir(o) })
                    .ToArray();
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Bundle", type = "searchset", total = matches.Length, entry = matches });
            }));
    }

    private void SetupDiagnosticReport()
    {
        _server
            .Given(Request.Create().WithPath("/fhir/DiagnosticReport").UsingPost())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var root = JsonDocument.Parse(req.Body ?? "{}").RootElement;
                var id = $"dr-{Guid.NewGuid():N}";
                string? loinc = null, codeText = null;
                if (root.TryGetProperty("code", out var code))
                {
                    if (code.TryGetProperty("coding", out var cd) && cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0
                        && cd[0].TryGetProperty("code", out var cc))
                    {
                        loinc = cc.GetString();
                    }
                    if (code.TryGetProperty("text", out var t))
                    {
                        codeText = t.GetString();
                    }
                }
                var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "preliminary" : "preliminary";
                var patient = ParseReference(root, "subject", "Patient/");
                var conclusion = root.TryGetProperty("conclusion", out var con) ? con.GetString() : null;
                var issued = root.TryGetProperty("issued", out var iss) ? iss.GetString() : null;
                string? performerDisplay = null;
                if (root.TryGetProperty("performer", out var perf) && perf.ValueKind == JsonValueKind.Array && perf.GetArrayLength() > 0
                    && perf[0].TryGetProperty("display", out var pd))
                {
                    performerDisplay = pd.GetString();
                }
                var resultIds = new List<string>();
                if (root.TryGetProperty("result", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in resArr.EnumerateArray())
                    {
                        if (r.TryGetProperty("reference", out var rref))
                        {
                            var s = rref.GetString() ?? "";
                            resultIds.Add(s.StartsWith("Observation/", StringComparison.Ordinal) ? s["Observation/".Length..] : s);
                        }
                    }
                }
                string? serviceRequestId = null;
                if (root.TryGetProperty("basedOn", out var boArr) && boArr.ValueKind == JsonValueKind.Array && boArr.GetArrayLength() > 0
                    && boArr[0].TryGetProperty("reference", out var bor))
                {
                    var s = bor.GetString() ?? "";
                    serviceRequestId = s.StartsWith("ServiceRequest/", StringComparison.Ordinal) ? s["ServiceRequest/".Length..] : s;
                }
                _diagnosticReports[id] = new FakeDiagnosticReport(id, patient, loinc, codeText, status, conclusion, performerDisplay, issued, resultIds, serviceRequestId);
                return JsonMessage(HttpStatusCode.Created, new { resourceType = "DiagnosticReport", id, meta = new { versionId = "1" } });
            }));

        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/DiagnosticReport/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                if (!_diagnosticReports.TryGetValue(id, out var dr))
                {
                    return JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
                }
                return JsonMessage(HttpStatusCode.OK, DiagnosticReportToFhir(dr));
            }));

        _server
            .Given(Request.Create().WithPath("/fhir/DiagnosticReport").UsingGet())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var subj = req.Query?.TryGetValue("subject", out var v) == true ? v.FirstOrDefault() : null;
                var patient = subj?.StartsWith("Patient/", StringComparison.Ordinal) == true ? subj["Patient/".Length..] : subj;
                var status = req.Query?.TryGetValue("status", out var sv) == true ? sv.FirstOrDefault() : null;
                var matches = _diagnosticReports.Values
                    .Where(d => (patient is null || d.PatientFhirId == patient) && (status is null || d.Status == status))
                    .Select(d => new { resource = DiagnosticReportToFhir(d) })
                    .ToArray();
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Bundle", type = "searchset", total = matches.Length, entry = matches });
            }));

        // PATCH (JSON Patch) de status para Observation / DiagnosticReport (LIS-6).
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Observation/[a-zA-Z0-9-]+$"))
                .UsingPatch())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                var newStatus = ParsePatchStatus(req.Body) ?? "final";
                if (_observations.TryGetValue(id, out var o))
                {
                    _observations[id] = o with { Status = newStatus };
                }
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "Observation", id });
            }));
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/DiagnosticReport/[a-zA-Z0-9-]+$"))
                .UsingPatch())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var id = req.Path.Split('/').Last();
                var (patchPath, patchValue) = ParsePatch(req.Body) ?? ("/status", null);
                if (_diagnosticReports.TryGetValue(id, out var d))
                {
                    _diagnosticReports[id] = patchPath == "/conclusion"
                        ? d with { Conclusion = patchValue }
                        : d with { Status = patchValue ?? "final" };
                }
                return JsonMessage(HttpStatusCode.OK, new { resourceType = "DiagnosticReport", id });
            }));
    }

    /// <summary>
    /// Parsea el cuerpo de un Observation FHIR a la forma que guarda el mock. Lo comparten el
    /// POST (alta) y el PUT (corrección de resultado): el PUT no existía, así que el camino de
    /// corrección nunca se había podido probar de punta a punta.
    /// </summary>
    private static FakeObservation ParseObservation(string id, string? body)
    {
        var root = JsonDocument.Parse(body ?? "{}").RootElement;
        string? loinc = null, codeText = null;
        if (root.TryGetProperty("code", out var code))
        {
            if (code.TryGetProperty("coding", out var cd) && cd.ValueKind == JsonValueKind.Array && cd.GetArrayLength() > 0
                && cd[0].TryGetProperty("code", out var cc))
            {
                loinc = cc.GetString();
            }
            if (code.TryGetProperty("text", out var t))
            {
                codeText = t.GetString();
            }
        }

        double? valueNum = null;
        string? unit = null, valueStr = null;
        if (root.TryGetProperty("valueQuantity", out var vq))
        {
            if (vq.TryGetProperty("value", out var v) && v.TryGetDouble(out var dv))
            {
                valueNum = dv;
            }
            if (vq.TryGetProperty("unit", out var u))
            {
                unit = u.GetString();
            }
        }
        else if (root.TryGetProperty("valueString", out var vs))
        {
            valueStr = vs.GetString();
        }

        string? flag = null;
        if (root.TryGetProperty("interpretation", out var ip) && ip.ValueKind == JsonValueKind.Array && ip.GetArrayLength() > 0
            && ip[0].TryGetProperty("coding", out var ic) && ic.ValueKind == JsonValueKind.Array && ic.GetArrayLength() > 0
            && ic[0].TryGetProperty("code", out var fc))
        {
            flag = fc.GetString();
        }

        double? low = null, high = null;
        if (root.TryGetProperty("referenceRange", out var rr) && rr.ValueKind == JsonValueKind.Array && rr.GetArrayLength() > 0)
        {
            if (rr[0].TryGetProperty("low", out var lo) && lo.TryGetProperty("value", out var lov) && lov.TryGetDouble(out var lod))
            {
                low = lod;
            }
            if (rr[0].TryGetProperty("high", out var hi) && hi.TryGetProperty("value", out var hiv) && hiv.TryGetDouble(out var hid))
            {
                high = hid;
            }
        }

        var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "preliminary" : "preliminary";
        var patient = ParseReference(root, "subject", "Patient/");
        var effective = root.TryGetProperty("effectiveDateTime", out var ed) ? ed.GetString() : null;

        var basedOn = ParseFirstReference(root, "basedOn", "ServiceRequest/");
        return new FakeObservation(
            id, patient, loinc, codeText, valueNum, unit, valueStr, flag, low, high, status, effective, basedOn);
    }

    /// <summary>Primera referencia de un array FHIR (<c>basedOn</c>, <c>partOf</c>, …).</summary>
    private static string? ParseFirstReference(JsonElement root, string property, string prefix)
    {
        if (!root.TryGetProperty(property, out var arr)
            || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        {
            return null;
        }
        if (!arr[0].TryGetProperty("reference", out var refEl))
        {
            return null;
        }
        var value = refEl.GetString();
        return value?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? value[prefix.Length..]
            : value;
    }

    private static string? ParsePatchStatus(string? body) =>
        ParsePatch(body) is { Path: "/status" } op ? op.Value : null;

    /// <summary>
    /// Primera operación del JSON Patch, con su path. Mirar el path importa: el mismo endpoint
    /// recibe patches de <c>/status</c> (liberación) y de <c>/conclusion</c> (comentario del
    /// validador), y leer solo el <c>value</c> guardaba el comentario como si fuera el status.
    /// </summary>
    private static (string Path, string? Value)? ParsePatch(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        try
        {
            var arr = JsonDocument.Parse(body).RootElement;
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
            {
                var path = arr[0].TryGetProperty("path", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                var value = arr[0].TryGetProperty("value", out var v) ? v.GetString() : null;
                return (path, value);
            }
        }
        catch (JsonException) { /* ignore */ }
        return null;
    }

    private static object ObservationToFhir(FakeObservation o) => new
    {
        resourceType = "Observation",
        id = o.Id,
        status = o.Status,
        subject = new { reference = $"Patient/{o.PatientFhirId}" },
        code = new
        {
            coding = o.Loinc is null ? null : new[] { new { system = "http://loinc.org", code = o.Loinc, display = o.CodeText } },
            text = o.CodeText
        },
        valueQuantity = o.ValueNumeric is null ? null : new { value = o.ValueNumeric, unit = o.Unit },
        valueString = o.ValueString,
        interpretation = o.Flag is null ? null : new[]
        {
            new { coding = new[] { new { system = "http://terminology.hl7.org/CodeSystem/v3-ObservationInterpretation", code = o.Flag } } }
        },
        referenceRange = (o.Low is null && o.High is null) ? null : new[]
        {
            new { low = o.Low is null ? null : new { value = o.Low }, high = o.High is null ? null : new { value = o.High } }
        },
        effectiveDateTime = o.Effective,
        basedOn = o.ServiceRequestFhirId is null
            ? null
            : new[] { new { reference = $"ServiceRequest/{o.ServiceRequestFhirId}" } }
    };

    private static object DiagnosticReportToFhir(FakeDiagnosticReport d) => new
    {
        resourceType = "DiagnosticReport",
        id = d.Id,
        status = d.Status,
        subject = new { reference = $"Patient/{d.PatientFhirId}" },
        code = new
        {
            coding = d.Loinc is null ? null : new[] { new { system = "http://loinc.org", code = d.Loinc, display = d.CodeText } },
            text = d.CodeText
        },
        conclusion = d.Conclusion,
        issued = d.Issued,
        performer = d.PerformerDisplay is null ? null : new[] { new { display = d.PerformerDisplay } },
        basedOn = d.ServiceRequestId is null ? null : new[] { new { reference = $"ServiceRequest/{d.ServiceRequestId}" } },
        result = d.ResultIds.Select(rid => new { reference = $"Observation/{rid}" }).ToArray()
    };

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

    /// <summary>Cadena de custodia creada: sobre qué recurso, qué actividad y quién la firmó.</summary>
    public sealed record CreatedProvenance(string Id, string TargetReference, string? ActivityCode, string? AgentWhoReference);

    private sealed record FakePractitioner(string Id, string Family, string Given, bool Active);

    private sealed record FakeServiceRequest(
        string Id, string PatientFhirId, string? Loinc, string? CodeText, string? Priority, string Status, string Intent,
        string? Encounter = null, string? Requisition = null,
        /// <summary>
        /// Código propio de la clínica, con su sistema. El mock guardaba solo <c>coding[0]</c> y al
        /// leer emitía <c>http://loinc.org</c> fijo — o sea que reproducía el mismo error que el
        /// cliente real tenía, y por eso ninguna prueba podía verlo. Un doble que miente sobre la
        /// forma del recurso no sirve para probar la lectura de esa forma.
        /// </summary>
        string? LocalCode = null, string? LocalSystem = null,
        /// <summary>
        /// El gateway siempre lo escribe (<c>BuildLabOrderPayload</c>), así que una orden real
        /// nunca llega sin él. El mock lo omitía, y con eso cualquier regla que dependa de la edad
        /// de la orden —como el margen de gracia de la reconciliación— quedaba sin poder probarse.
        /// </summary>
        string? AuthoredOn = null);

    private sealed record FakeObservation(
        string Id, string PatientFhirId, string? Loinc, string? CodeText, double? ValueNumeric, string? Unit,
        string? ValueString, string? Flag, double? Low, double? High, string Status, string? Effective,
        /// <summary>
        /// <c>basedOn</c> → la orden que originó el resultado. El mock no lo guardaba, así que
        /// devolvía siempre null y cualquier lógica que dependa del vínculo resultado↔orden
        /// quedaba sin poder probarse — la corrección lo necesita para cerrar la devolución.
        /// </summary>
        string? ServiceRequestFhirId = null);

    private sealed record FakeDiagnosticReport(
        string Id, string PatientFhirId, string? Loinc, string? CodeText, string Status, string? Conclusion,
        string? PerformerDisplay, string? Issued, IReadOnlyList<string> ResultIds, string? ServiceRequestId);

    private sealed record FakePractitionerRole(
        string Id,
        string PractitionerFhirId,
        string? SpecialtyCode,
        string? SpecialtyDisplay,
        bool Active,
        string? OrganizationFhirId = null,
        IReadOnlyList<string>? Locations = null);
}
