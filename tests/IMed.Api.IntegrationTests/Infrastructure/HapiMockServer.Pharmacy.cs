using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using WireMock;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Recetas para la farmacia (Stream B): lectura y búsqueda de <c>MedicationRequest</c> sobre el
/// almacén genérico de documentos. Se registran a pedido (<see cref="EnablePharmacy"/>) para no
/// tocar el constructor compartido del mock.
///
/// <para>La búsqueda entiende solo lo que usa la cola de farmacia (<c>status</c>,
/// <c>authoredon=ge…</c>, <c>_sort</c>, <c>_count</c>); cualquier otro parámetro responde 400, como
/// el resto del almacén: un filtro ignorado en silencio escondería un bug.</para>
/// </summary>
public sealed partial class HapiMockServer
{
    private bool _pharmacyEnabled;
    private readonly object _pharmacyLock = new();

    public void EnablePharmacy()
    {
        lock (_pharmacyLock)
        {
            if (_pharmacyEnabled)
            {
                return;
            }
            _pharmacyEnabled = true;
        }

        _server
            .Given(Request.Create()
                .WithPath(new WireMock.Matchers.RegexMatcher("^/fhir/MedicationRequest/[a-zA-Z0-9-]+$"))
                .UsingGet())
            .RespondWith(Response.Create().WithCallback(req => ReadDocument("MedicationRequest", req.Path.Split('/').Last())));

        _server
            .Given(Request.Create().WithPath("/fhir/MedicationRequest").UsingGet())
            .RespondWith(Response.Create().WithCallback(SearchMedicationRequests));
    }

    /// <summary>Siembra una receta activa con código ATC, como la escribe el cliente real.</summary>
    public string SeedMedicationRequest(
        string patientId, string encounterId, string atcCode, string display, decimal? quantity,
        string status = "active", string? unit = "tabletas", DateTimeOffset? authoredOn = null, string? group = null) =>
        SeedResource(new JsonObject
        {
            ["resourceType"] = "MedicationRequest",
            ["status"] = status,
            ["intent"] = "order",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
            ["encounter"] = new JsonObject { ["reference"] = $"Encounter/{encounterId}" },
            ["authoredOn"] = (authoredOn ?? DateTimeOffset.UtcNow).ToString("o", CultureInfo.InvariantCulture),
            ["groupIdentifier"] = group is null ? null : new JsonObject { ["value"] = group },
            ["medication"] = new JsonObject
            {
                ["concept"] = new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = "http://www.whocc.no/atc", ["code"] = atcCode, ["display"] = display,
                    }),
                    ["text"] = display,
                },
            },
            ["dosageInstruction"] = new JsonArray(new JsonObject { ["text"] = "1 cada 8 horas por 7 días" }),
            ["dispenseRequest"] = quantity is null ? null : new JsonObject
            {
                ["quantity"] = new JsonObject { ["value"] = quantity.Value, ["unit"] = unit },
            },
        });

    /// <summary>Siembra una consulta en una sede (FAC-9: <c>Location/sede-{id}</c>).</summary>
    public string SeedEncounterAt(Guid locationId, string patientId, string status = "finished") =>
        SeedResource(new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = status,
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
            ["location"] = new JsonArray(new JsonObject
            {
                ["location"] = new JsonObject { ["reference"] = $"Location/sede-{locationId:N}" },
            }),
        });

    private ResponseMessage SearchMedicationRequests(IRequestMessage req)
    {
        var query = (req.Query ?? new Dictionary<string, WireMock.Types.WireMockList<string>>())
            .ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.Ordinal);
        lock (_docsLock)
        {
            IEnumerable<JsonObject> q = Store("MedicationRequest").Values;
            var count = int.MaxValue;
            foreach (var (name, value) in query)
            {
                switch (name)
                {
                    case "status":
                        var estados = value.Split(',');
                        q = q.Where(r => estados.Contains((string?)r["status"]));
                        break;
                    case "authoredon" when value.StartsWith("ge", StringComparison.Ordinal):
                        var desde = DateTimeOffset.Parse(value[2..], CultureInfo.InvariantCulture);
                        q = q.Where(r => DateTimeOffset.Parse((string)r["authoredOn"]!, CultureInfo.InvariantCulture) >= desde);
                        break;
                    case "_sort":
                        q = q.OrderByDescending(r => (string?)r["authoredOn"]);
                        break;
                    case "_count":
                        count = int.Parse(value, CultureInfo.InvariantCulture);
                        break;
                    default:
                        return JsonMessage(HttpStatusCode.BadRequest, new { error = $"El mock no entiende el parámetro '{name}'." });
                }
            }
            var entries = q.Take(count).Select(r => new JsonObject { ["resource"] = r.DeepClone() }).ToArray<JsonNode?>();
            return JsonMessage(HttpStatusCode.OK, new JsonObject
            {
                ["resourceType"] = "Bundle",
                ["type"] = "searchset",
                ["total"] = entries.Length,
                ["entry"] = new JsonArray(entries),
            });
        }
    }
}
