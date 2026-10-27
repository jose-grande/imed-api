using System.Net;
using System.Text.Json.Nodes;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Almacén genérico de recursos del mock (Épica P): guarda el JSON tal como llega, versiona cada
/// escritura y entiende transacciones con <c>ifNoneExist</c> e <c>ifMatch</c>.
///
/// <para><b>Por qué genérico.</b> Los mocks tipados de arriba guardan solo los campos que su prueba
/// mira, y ya pasó dos veces que eso escondiera el bug (ESTADO, 2026-08-16): un doble que miente
/// sobre la forma del recurso no sirve para probar la lectura de esa forma. Acá el recurso viaja
/// entero, y una búsqueda con un parámetro que el mock no entiende responde 400 en vez de devolver
/// "todo" — un filtro ignorado en silencio es justo lo que no tiene que poder pasar.</para>
/// </summary>
public sealed partial class HapiMockServer
{
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _docs = new(StringComparer.Ordinal);
    private readonly object _docsLock = new();

    /// <summary>Último Bundle de transacción recibido, para afirmar sobre lo que se mandó.</summary>
    public JsonObject? LastTransaction { get; private set; }

    /// <summary>Cantidad de transacciones recibidas.</summary>
    public int TransactionCount { get; private set; }

    private void ResetDocuments()
    {
        lock (_docsLock)
        {
            _docs.Clear();
            LastTransaction = null;
            TransactionCount = 0;
        }
    }

    /// <summary>Guarda un recurso como si ya existiera en HAPI. Devuelve su id.</summary>
    public string SeedResource(JsonObject resource)
    {
        lock (_docsLock)
        {
            var type = (string)resource["resourceType"]!;
            var id = (string?)resource["id"] ?? $"{type.ToLowerInvariant()}-{Guid.NewGuid():N}";
            resource["id"] = id;
            // Se conserva un `lastUpdated` dado: hay reglas que dependen de la edad del recurso.
            var lastUpdated = (string?)resource["meta"]?["lastUpdated"];
            resource["meta"] = new JsonObject { ["versionId"] = "1" };
            if (lastUpdated is not null) { resource["meta"]!["lastUpdated"] = lastUpdated; }
            Store(type)[id] = resource;
            return id;
        }
    }

    /// <summary>Cita en la fila (o fuera de ella) para las pruebas de preclínica.</summary>
    public string SeedAppointment(string status, string patientId, string? id = null) => SeedResource(new JsonObject
    {
        ["resourceType"] = "Appointment",
        ["id"] = id,
        ["status"] = status,
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
        ["participant"] = new JsonArray(
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Patient/{patientId}" }, ["status"] = "accepted" }),
        ["start"] = "2026-10-04T15:00:00Z",
        ["end"] = "2026-10-04T15:30:00Z",
    });

    /// <summary>Copia del recurso guardado, o <c>null</c>.</summary>
    public JsonObject? GetResource(string type, string id)
    {
        lock (_docsLock)
        {
            return Store(type).TryGetValue(id, out var r) ? (JsonObject)r.DeepClone() : null;
        }
    }

    /// <summary>Copias de todos los recursos de un tipo.</summary>
    public IReadOnlyList<JsonObject> Resources(string type)
    {
        lock (_docsLock)
        {
            return Store(type).Values.Select(r => (JsonObject)r.DeepClone()).ToList();
        }
    }

    private Dictionary<string, JsonObject> Store(string type)
    {
        if (!_docs.TryGetValue(type, out var store))
        {
            store = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _docs[type] = store;
        }
        return store;
    }

    private void SetupDocuments()
    {
        // Sedes y razones sociales (FAC-9): se escriben con PUT sobre un id determinístico. Como
        // HAPI, el PUT crea si no existe y versiona si existe. La integridad referencial se
        // verifica igual que en HAPI: un Location cuya razón social no está todavía se rechaza.
        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/(Location|Organization|Encounter)/[a-zA-Z0-9-]+$"))
                .UsingPut())
            .RespondWith(Response.Create().WithCallback(req =>
            {
                var parts = req.Path.Split('/');
                var (type, id) = (parts[^2], parts[^1]);
                var resource = JsonNode.Parse(req.Body ?? "{}")!.AsObject();
                lock (_docsLock)
                {
                    var managing = (string?)resource["managingOrganization"]?["reference"];
                    if (type == "Location" && managing is not null
                        && !Store("Organization").ContainsKey(managing["Organization/".Length..]))
                    {
                        return JsonMessage(HttpStatusCode.UnprocessableEntity, new
                        {
                            error = $"HAPI-1094: Resource {managing} not found",
                        });
                    }

                    var version = Store(type).TryGetValue(id, out var previo)
                        ? int.Parse((string?)previo["meta"]?["versionId"] ?? "1") + 1
                        : 1;
                    resource["id"] = id;
                    resource["meta"] = new JsonObject { ["versionId"] = version.ToString() };
                    Store(type)[id] = resource;
                    return JsonMessage(version == 1 ? HttpStatusCode.Created : HttpStatusCode.OK, resource);
                }
            }));

        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Location/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req => ReadDocument("Location", req.Path.Split('/').Last())));

        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/Appointment/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req => ReadDocument("Appointment", req.Path.Split('/').Last())));

        _server
            .Given(Request.Create().WithPath("/fhir/Encounter").UsingGet())
            .RespondWith(Response.Create().WithCallback(req => SearchDocuments("Encounter", req)));

        _server
            .Given(Request.Create().WithPath("/fhir/AllergyIntolerance").UsingGet())
            .RespondWith(Response.Create().WithCallback(req => SearchDocuments("AllergyIntolerance", req)));

        _server
            .Given(Request.Create().WithPath("/fhir/MedicationStatement").UsingGet())
            .RespondWith(Response.Create().WithCallback(req => SearchDocuments("MedicationStatement", req)));

        _server
            .Given(Request.Create().WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/?$")).UsingPost())
            .RespondWith(Response.Create().WithCallback(req => ExecuteTransaction(req.Body)));
    }

    private ResponseMessage ReadDocument(string type, string id)
    {
        lock (_docsLock)
        {
            return Store(type).TryGetValue(id, out var r)
                ? JsonMessage(HttpStatusCode.OK, r)
                : JsonMessage(HttpStatusCode.NotFound, new { error = "not found" });
        }
    }

    private ResponseMessage SearchDocuments(string type, IRequestMessage req)
    {
        var query = (req.Query ?? new Dictionary<string, WireMock.Types.WireMockList<string>>())
            .ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.Ordinal);
        lock (_docsLock)
        {
            try
            {
                var offset = query.TryGetValue("_offset", out var o) && int.TryParse(o, out var oi) ? oi : 0;
                var count = query.TryGetValue("_count", out var c) && int.TryParse(c, out var ci) ? ci : int.MaxValue;
                var matches = Store(type).Values
                    .Where(r => Matches(r, query))
                    .Skip(offset)
                    .Take(count)
                    .Select(r => new JsonObject { ["resource"] = r.DeepClone() })
                    .ToArray<JsonNode?>();
                return JsonMessage(HttpStatusCode.OK, new JsonObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"] = "searchset",
                    ["total"] = matches.Length,
                    ["entry"] = new JsonArray(matches),
                });
            }
            catch (NotSupportedException ex)
            {
                return JsonMessage(HttpStatusCode.BadRequest, new { error = ex.Message });
            }
        }
    }

    /// <summary>Filtro de búsqueda FHIR reducido a los parámetros que usa el código bajo prueba.</summary>
    private static bool Matches(JsonObject r, IReadOnlyDictionary<string, string> query)
    {
        foreach (var (name, raw) in query)
        {
            var values = raw.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var ok = name switch
            {
                "_count" or "_sort" or "_offset" => true,
                "status" => values.Contains((string?)r["status"]),
                "appointment" => (r["appointment"] as JsonArray)?.Any(a => values.Contains((string?)a?["reference"])) == true,
                "subject" => values.Contains((string?)r["subject"]?["reference"]),
                "patient" => values.Contains((string?)r["patient"]?["reference"])
                    || values.Contains((string?)r["subject"]?["reference"]),
                "encounter" => values.Contains((string?)r["encounter"]?["reference"]),
                "clinical-status" => values.Contains((string?)r["clinicalStatus"]?["coding"]?[0]?["code"]),
                "category" => (r["category"] as JsonArray)?.Any(c =>
                    (c?["coding"] as JsonArray)?.Any(cd => values.Contains((string?)cd?["code"])) == true) == true,
                _ => throw new NotSupportedException($"El mock no entiende el parámetro '{name}'."),
            };
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : "", StringComparer.Ordinal);

    /// <summary>
    /// Transacción: valida todo antes de escribir nada (un <c>ifMatch</c> vencido devuelve 412 y
    /// no toca el almacén), resuelve las referencias <c>urn:uuid</c> y versiona cada escritura.
    /// </summary>
    private ResponseMessage ExecuteTransaction(string? body)
    {
        if (JsonNode.Parse(body ?? "{}") is not JsonObject bundle
            || (string?)bundle["type"] != "transaction"
            || bundle["entry"] is not JsonArray entries)
        {
            return JsonMessage(HttpStatusCode.BadRequest, new { error = "No es un Bundle transaction." });
        }

        lock (_docsLock)
        {
            LastTransaction = (JsonObject)bundle.DeepClone();
            TransactionCount++;

            var urns = new Dictionary<string, string>(StringComparer.Ordinal);
            var plan = new List<(string Type, string Id, JsonObject? Resource, string Status)>();

            foreach (var entry in entries)
            {
                var method = (string?)entry?["request"]?["method"];
                var url = (string?)entry?["request"]?["url"] ?? "";
                var fullUrl = (string?)entry?["fullUrl"];
                var resource = entry?["resource"] as JsonObject;

                if (method == "POST")
                {
                    var ifNoneExist = (string?)entry?["request"]?["ifNoneExist"];
                    if (ifNoneExist is not null)
                    {
                        var existing = Store(url).Values.FirstOrDefault(r => Matches(r, ParseQuery(ifNoneExist)));
                        if (existing is not null)
                        {
                            var existingId = (string)existing["id"]!;
                            if (fullUrl is not null) { urns[fullUrl] = $"{url}/{existingId}"; }
                            plan.Add((url, existingId, null, "200 OK"));
                            continue;
                        }
                    }
                    var id = $"{url.ToLowerInvariant()}-{Guid.NewGuid():N}";
                    if (fullUrl is not null) { urns[fullUrl] = $"{url}/{id}"; }
                    plan.Add((url, id, resource, "201 Created"));
                }
                else if (method == "PUT")
                {
                    var parts = url.Split('/');
                    var (type, id) = (parts[0], parts[1]);
                    var ifMatch = (string?)entry?["request"]?["ifMatch"];
                    if (ifMatch is not null)
                    {
                        var current = Store(type).TryGetValue(id, out var cur) ? (string?)cur["meta"]?["versionId"] : null;
                        if ($"W/\"{current}\"" != ifMatch)
                        {
                            return JsonMessage(HttpStatusCode.PreconditionFailed, new
                            {
                                resourceType = "OperationOutcome",
                                issue = new[] { new { severity = "error", code = "conflict", diagnostics = $"{url}: versión {current}, se esperaba {ifMatch}" } },
                            });
                        }
                    }
                    if (fullUrl is not null) { urns[fullUrl] = url; }
                    plan.Add((type, id, resource, "200 OK"));
                }
                else
                {
                    return JsonMessage(HttpStatusCode.BadRequest, new { error = $"Método no soportado: {method}" });
                }
            }

            var responses = new JsonArray();
            foreach (var (type, id, resource, status) in plan)
            {
                var store = Store(type);
                if (resource is not null)
                {
                    var copy = (JsonObject)resource.DeepClone();
                    ReplaceUrns(copy, urns);
                    var version = store.TryGetValue(id, out var prev)
                        && int.TryParse((string?)prev["meta"]?["versionId"], out var v) ? v + 1 : 1;
                    copy["id"] = id;
                    copy["meta"] = new JsonObject
                    {
                        ["versionId"] = version.ToString(),
                        ["lastUpdated"] = DateTimeOffset.UtcNow.ToString("o"),
                    };
                    store[id] = copy;
                }
                var current = (string?)store[id]["meta"]?["versionId"] ?? "1";
                responses.Add(new JsonObject
                {
                    ["response"] = new JsonObject
                    {
                        ["status"] = status,
                        ["location"] = $"{type}/{id}/_history/{current}",
                    },
                });
            }

            return JsonMessage(HttpStatusCode.OK, new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "transaction-response",
                ["entry"] = responses,
            });
        }
    }

    private static void ReplaceUrns(JsonNode node, IReadOnlyDictionary<string, string> urns)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kv => kv.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue val && val.TryGetValue<string>(out var s) && urns.TryGetValue(s, out var target))
                    {
                        obj[key] = target;
                    }
                    else if (child is not null)
                    {
                        ReplaceUrns(child, urns);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var child in arr)
                {
                    if (child is not null) { ReplaceUrns(child, urns); }
                }
                break;
        }
    }
}
