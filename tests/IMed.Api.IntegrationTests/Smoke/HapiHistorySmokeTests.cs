using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Preclinic;
using IMed.Api.Infrastructure.Hapi;
using IMed.Api.Infrastructure.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IMed.Api.IntegrationTests.Smoke;

/// <summary>Corre solo con <c>IMED_HAPI_SMOKE=1</c>: necesita el HAPI y el Keycloak locales levantados.</summary>
public sealed class SmokeFactAttribute : FactAttribute
{
    public SmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("IMED_HAPI_SMOKE") != "1")
        {
            Skip = "Humo contra HAPI real: definir IMED_HAPI_SMOKE=1 con la pila local levantada.";
        }
    }
}

/// <summary>
/// Humo de la Épica P-1 contra el HAPI real (DoD desde la Épica L: un recurso o una forma nueva se
/// prueba contra el servidor, no solo contra el mock — el mock acepta lo que HAPI rechaza).
///
/// <para>Ejercita el código de producción —<see cref="PreclinicFhir.Build"/>, la transacción, la
/// creación condicional, <c>ifMatch</c>— y las formas que arma el cierre de consulta en el
/// navegador: <c>List</c> R5, <c>Provenance</c> con <c>encounter</c>, alergia
/// <c>entered-in-error</c> sin estado clínico. Escribe en una partición propia, nunca en las de dev.</para>
/// </summary>
public sealed class HapiHistorySmokeTests
{
    /// <summary>Organización ficticia: su partición es solo para este humo.</summary>
    private static readonly Guid SmokeOrg = Guid.Parse("00000000-0000-4000-8000-00000000p1sm".Replace("p1sm", "51a1"));
    private static string Partition => $"org-{SmokeOrg}";

    private static (HapiFhirClient Client, KeycloakServiceAccountTokenProvider Tokens) CreateClient()
    {
        var settings = FindDevSettings();
        var config = new ConfigurationBuilder().AddJsonFile(settings).Build();
        var keycloak = config.GetSection("Keycloak").Get<KeycloakOptions>()!;
        var tokens = new KeycloakServiceAccountTokenProvider(
            new SimpleHttpClientFactory(), Options.Create(keycloak),
            NullLogger<KeycloakServiceAccountTokenProvider>.Instance, TimeProvider.System);
        var http = new HttpClient { BaseAddress = new Uri(config["HapiFhir:BaseUrl"]!.TrimEnd('/') + "/") };
        return (new HapiFhirClient(http, tokens, new ConsoleLogger<HapiFhirClient>()), tokens);
    }

    private static string FindDevSettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "IMed.Api.WebApi", "appsettings.Development.json");
            if (File.Exists(candidate)) { return candidate; }
            dir = dir.Parent;
        }
        throw new FileNotFoundException("No se encontró appsettings.Development.json de imed-api.");
    }

    /// <summary>El cuerpo del error de HAPI es lo que explica un 422; sin esto el humo solo dice "falló".</summary>
    private sealed class ConsoleLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) { Console.WriteLine($"[{logLevel}] {formatter(state, exception)}"); }
        }
    }

    private sealed class SimpleHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private static async Task<string> CreateAsync(HapiFhirClient hapi, JsonObject resource)
    {
        var type = (string)resource["resourceType"]!;
        var results = await hapi.ExecuteTransactionAsync(new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(new JsonObject
            {
                ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
                ["resource"] = resource,
                ["request"] = new JsonObject { ["method"] = "POST", ["url"] = type },
            }),
        }, Partition);
        return results[0].ResourceId!;
    }

    [SmokeFact]
    public async Task LaPreclinicaYElCierreConAntecedentes_pasanContraHapiReal()
    {
        var (hapi, tokens) = CreateClient();
        using var _ = tokens;
        await hapi.CreatePartitionAsync(Partition);

        // --- Paciente y cita en la fila ------------------------------------------------------
        var patient = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Patient",
            ["name"] = new JsonArray(new JsonObject { ["family"] = "Humo", ["given"] = new JsonArray("P1") }),
        });
        var start = DateTimeOffset.UtcNow;
        var appointment = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Appointment",
            ["status"] = "arrived",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
            ["participant"] = new JsonArray(new JsonObject
            {
                ["actor"] = new JsonObject { ["reference"] = $"Patient/{patient}" }, ["status"] = "accepted",
            }),
            ["start"] = start.ToString("o"),
            ["end"] = start.AddMinutes(30).ToString("o"),
        });

        // --- Preclínica: abre el encuentro y no duplica ---------------------------------------
        // Con practicante: es lo que agrega `participant` a las alergias y `performer` a los signos
        // vitales. Sin él, el humo nunca mandaba esos campos, y `recorder` (R4) llegó a dev con 422.
        var auxiliar = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Practitioner",
            ["name"] = new JsonArray(new JsonObject { ["family"] = "Humo", ["given"] = new JsonArray("Auxiliar") }),
        });
        var submit = new SubmitPreclinicHandler(hapi, TimeProvider.System);
        var first = await submit.HandleAsync(new SubmitPreclinicCommand(
            SmokeOrg, appointment, auxiliar,
            new PreclinicVitalsInput(70m, 170m, 120m, 80m, 36.6m, 72m),
            [new PreclinicCatalogAllergyInput("764146007", "Penicilina", "medication")],
            ["Látex"], NoKnownAllergies: false));
        var second = await submit.HandleAsync(new SubmitPreclinicCommand(
            SmokeOrg, appointment, null,
            new PreclinicVitalsInput(71m, null, null, null, null, null),
            [new PreclinicCatalogAllergyInput("764146007", "Penicilina", "medication")],
            ["LATEX "], NoKnownAllergies: false));

        second.EncounterFhirId.Should().Be(first.EncounterFhirId, "la segunda toma reusa el encuentro (ifNoneExist)");
        second.AllergiesCreated.Should().Be(0);
        second.SkippedDuplicates.Should().HaveCount(2);

        var encounter = await hapi.ReadResourceAsync("Encounter", first.EncounterFhirId, Partition);
        ((string?)encounter!["status"]).Should().Be("planned");
        var appt = await hapi.ReadResourceAsync("Appointment", appointment, Partition);
        ((string?)appt!["status"]).Should().Be("checked-in");

        var ctx = await new GetPreclinicContextHandler(hapi).HandleAsync(SmokeOrg, appointment);
        ctx.Vitals.Should().HaveCount(7);
        ctx.Allergies.Should().HaveCount(2);

        // --- Cierre: confirmar una, marcar otra como error, lista y constancia -----------------
        var allergies = await hapi.SearchResourcesAsync(
            "AllergyIntolerance", $"patient=Patient/{patient}&clinical-status=active", Partition);
        var penicilina = allergies.Single(a => (string?)a["code"]!["text"] == "Penicilina");
        var latex = allergies.Single(a => (string?)a["code"]!["text"] == "Látex");

        var confirmed = (JsonObject)penicilina.DeepClone();
        confirmed.Remove("meta");
        confirmed["verificationStatus"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", ["code"] = "confirmed",
            }),
        };
        var error = (JsonObject)latex.DeepClone();
        error.Remove("meta");
        error.Remove("clinicalStatus");
        error["verificationStatus"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification", ["code"] = "entered-in-error",
            }),
        };
        var encounterRef = $"Encounter/{first.EncounterFhirId}";
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var listUrn = $"urn:uuid:{Guid.NewGuid()}";
        var closeBundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(
                Put(confirmed, $"AllergyIntolerance/{penicilina["id"]}", (string?)penicilina["meta"]!["versionId"]),
                Put(error, $"AllergyIntolerance/{latex["id"]}", (string?)latex["meta"]!["versionId"]),
                new JsonObject
                {
                    ["fullUrl"] = listUrn,
                    ["resource"] = new JsonObject
                    {
                        ["resourceType"] = "List",
                        ["status"] = "current",
                        ["mode"] = "working",
                        ["title"] = "Alergias",
                        ["code"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = "http://loinc.org", ["code"] = "48765-2", ["display"] = "Alergias",
                            }),
                        },
                        ["subject"] = new JsonArray(new JsonObject { ["reference"] = $"Patient/{patient}" }),
                        ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                        ["date"] = now,
                        ["entry"] = new JsonArray(new JsonObject
                        {
                            ["item"] = new JsonObject { ["reference"] = $"AllergyIntolerance/{penicilina["id"]}" },
                        }),
                    },
                    ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "List" },
                },
                new JsonObject
                {
                    ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
                    ["resource"] = new JsonObject
                    {
                        ["resourceType"] = "Provenance",
                        ["target"] = new JsonArray(
                            new JsonObject { ["reference"] = $"AllergyIntolerance/{penicilina["id"]}" },
                            new JsonObject { ["reference"] = $"AllergyIntolerance/{latex["id"]}" },
                            new JsonObject { ["reference"] = listUrn }),
                        ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                        ["recorded"] = now,
                        ["activity"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = "http://terminology.hl7.org/CodeSystem/v3-DataOperation", ["code"] = "UPDATE",
                            }),
                            ["text"] = "Actualización de antecedentes en consulta",
                        },
                        ["agent"] = new JsonArray(new JsonObject
                        {
                            ["who"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
                        }),
                    },
                    ["request"] = new JsonObject { ["method"] = "POST", ["url"] = "Provenance" },
                }),
        };

        var closed = await hapi.ExecuteTransactionAsync(closeBundle, Partition);
        closed.Should().HaveCount(4);
        closed[3].ResourceType.Should().Be("Provenance");

        var stillActive = await hapi.SearchResourcesAsync(
            "AllergyIntolerance", $"patient=Patient/{patient}&clinical-status=active", Partition);
        stillActive.Select(a => (string?)a["code"]!["text"]).Should().BeEquivalentTo(["Penicilina"]);

        // --- Limpieza de duplicados (paginada con _offset) ------------------------------------
        await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", ["code"] = "active",
                }),
            },
            ["code"] = new JsonObject { ["text"] = "PENICILINA" }, // el duplicado de antes de P-1, en texto libre
            ["patient"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
        });
        var dedupe = new IMed.Api.Application.Allergies.AllergyDedupeHandler(hapi);
        var dry = await dedupe.HandleAsync(SmokeOrg, apply: false);
        // "PENICILINA" en texto libre no comparte código con la de catálogo: no es el mismo grupo.
        dry.Groups.Where(g => g.PatientFhirId == patient).Should().BeEmpty();
        await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", ["code"] = "active",
                }),
            },
            ["code"] = new JsonObject { ["text"] = " penicilina" },
            ["patient"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
        });
        var applied = await dedupe.HandleAsync(SmokeOrg, apply: true);
        applied.Groups.Where(g => g.PatientFhirId == patient).Should().ContainSingle()
            .Which.DuplicateIds.Should().HaveCount(1);

        // --- Una versión vencida tumba la transacción entera ----------------------------------
        var stale = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(Put(
                (JsonObject)confirmed.DeepClone(), $"AllergyIntolerance/{penicilina["id"]}",
                (string?)penicilina["meta"]!["versionId"])),
        };
        var act = () => hapi.ExecuteTransactionAsync(stale, Partition);
        await act.Should().ThrowAsync<FhirVersionConflictException>();
    }

    [SmokeFact]
    public async Task ProblemasYMedicacion_pasanContraHapiReal()
    {
        var (hapi, tokens) = CreateClient();
        using var _ = tokens;
        await hapi.CreatePartitionAsync(Partition);

        var patient = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Patient",
            ["name"] = new JsonArray(new JsonObject { ["family"] = "Humo", ["given"] = new JsonArray("P2") }),
        });
        var start = DateTimeOffset.UtcNow;
        var appointment = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Appointment",
            ["status"] = "arrived",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
            ["participant"] = new JsonArray(new JsonObject
            {
                ["actor"] = new JsonObject { ["reference"] = $"Patient/{patient}" }, ["status"] = "accepted",
            }),
            ["start"] = start.ToString("o"),
            ["end"] = start.AddMinutes(30).ToString("o"),
        });

        // Preclínica con medicación declarada: MedicationStatement R5 en borrador.
        var pre = await new SubmitPreclinicHandler(hapi, TimeProvider.System).HandleAsync(new SubmitPreclinicCommand(
            SmokeOrg, appointment, null, null, null, null, NoKnownAllergies: false,
            Medications: [new PreclinicMedicationInput("C09AA02", "Enalapril", "10 mg c/24 h")]));
        pre.MedicationsCreated.Should().Be(1);
        var ctx = await new GetPreclinicContextHandler(hapi).HandleAsync(SmokeOrg, appointment);
        ctx.Medications.Should().ContainSingle().Which.Status.Should().Be("draft");

        // Cierre como lo arma el navegador: problema nuevo con participant (R5), medicación
        // conciliada, otra suspendida, listas y constancia.
        var draftStatement = (await hapi.SearchResourcesAsync(
            "MedicationStatement", $"subject=Patient/{patient}&status=draft", Partition)).Single();
        var conciliated = (JsonObject)draftStatement.DeepClone();
        conciliated.Remove("meta");
        conciliated["status"] = "recorded";
        conciliated["dosage"] = new JsonArray(new JsonObject { ["text"] = "20 mg c/24 h" });

        var encounterRef = $"Encounter/{pre.EncounterFhirId}";
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var conditionUrn = $"urn:uuid:{Guid.NewGuid()}";
        var metforminaUrn = $"urn:uuid:{Guid.NewGuid()}";
        JsonObject Coding(string system, string code) => new()
        {
            ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }),
        };
        JsonObject List(string code, string title, params string[] items) => new()
        {
            ["resourceType"] = "List",
            ["status"] = "current",
            ["mode"] = "working",
            ["title"] = title,
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://loinc.org", ["code"] = code, ["display"] = title }),
            },
            ["subject"] = new JsonArray(new JsonObject { ["reference"] = $"Patient/{patient}" }),
            ["encounter"] = new JsonObject { ["reference"] = encounterRef },
            ["date"] = now,
            ["entry"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject
            {
                ["item"] = new JsonObject { ["reference"] = i },
            }).ToArray()),
        };
        JsonObject Post(string urn, JsonObject resource, string type) => new()
        {
            ["fullUrl"] = urn,
            ["resource"] = resource,
            ["request"] = new JsonObject { ["method"] = "POST", ["url"] = type },
        };

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(
                Post(conditionUrn, new JsonObject
                {
                    ["resourceType"] = "Condition",
                    ["clinicalStatus"] = Coding("http://terminology.hl7.org/CodeSystem/condition-clinical", "active"),
                    ["verificationStatus"] = Coding("http://terminology.hl7.org/CodeSystem/condition-ver-status", "confirmed"),
                    ["category"] = new JsonArray(Coding("http://terminology.hl7.org/CodeSystem/condition-category", "problem-list-item")),
                    ["code"] = new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = "http://id.who.int/icd11/mms", ["code"] = "BA00", ["display"] = "Hipertensión esencial",
                        }),
                        ["text"] = "Hipertensión esencial",
                    },
                    ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
                    ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                    ["recordedDate"] = now,
                    ["participant"] = new JsonArray(new JsonObject
                    {
                        ["function"] = Coding("http://terminology.hl7.org/CodeSystem/provenance-participant-type", "author"),
                        ["actor"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
                    }),
                }, "Condition"),
                Put(conciliated, $"MedicationStatement/{draftStatement["id"]}", (string?)draftStatement["meta"]!["versionId"]),
                Post(metforminaUrn, new JsonObject
                {
                    ["resourceType"] = "MedicationStatement",
                    ["status"] = "recorded",
                    ["medication"] = new JsonObject
                    {
                        ["concept"] = new JsonObject { ["text"] = "Metformina" },
                    },
                    ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
                    ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                    ["dateAsserted"] = now,
                    ["adherence"] = new JsonObject
                    {
                        ["code"] = Coding("http://hl7.org/fhir/CodeSystem/medication-statement-adherence", "stopped"),
                    },
                    ["effectivePeriod"] = new JsonObject { ["end"] = now },
                }, "MedicationStatement"),
                Post($"urn:uuid:{Guid.NewGuid()}", List("11450-4", "Problemas", conditionUrn), "List"),
                Post($"urn:uuid:{Guid.NewGuid()}", List("10160-0", "Medicación habitual",
                    $"MedicationStatement/{draftStatement["id"]}"), "List"),
                Post($"urn:uuid:{Guid.NewGuid()}", new JsonObject
                {
                    ["resourceType"] = "Provenance",
                    ["target"] = new JsonArray(
                        new JsonObject { ["reference"] = conditionUrn },
                        new JsonObject { ["reference"] = $"MedicationStatement/{draftStatement["id"]}" },
                        new JsonObject { ["reference"] = metforminaUrn }),
                    ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                    ["recorded"] = now,
                    ["agent"] = new JsonArray(new JsonObject { ["who"] = new JsonObject { ["reference"] = $"Patient/{patient}" } }),
                }, "Provenance")),
        };

        var results = await hapi.ExecuteTransactionAsync(bundle, Partition);
        results.Should().HaveCount(6);

        var problems = await hapi.SearchResourcesAsync(
            "Condition", $"subject=Patient/{patient}&category=problem-list-item", Partition);
        problems.Should().ContainSingle();
        var current = (await hapi.SearchResourcesAsync(
            "MedicationStatement", $"subject=Patient/{patient}&status=recorded,draft", Partition))
            .Select(PreclinicFhir.ParseMedication).Where(m => m is not null).ToList();
        current.Should().ContainSingle().Which!.DosageText.Should().Be("20 mg c/24 h");
    }

    /// <summary>
    /// P-3: las formas que arman los adaptadores del navegador (record-adapters.ts y
    /// fact-history.ts), copiadas tal cual, y las búsquedas con las que el panel las relee.
    /// </summary>
    [SmokeFact]
    public async Task ElRestoDeLosAntecedentes_pasaContraHapiReal()
    {
        var (hapi, tokens) = CreateClient();
        using var _ = tokens;
        await hapi.CreatePartitionAsync(Partition);

        var patient = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Patient",
            ["gender"] = "female",
            ["name"] = new JsonArray(new JsonObject { ["family"] = "Humo", ["given"] = new JsonArray("P3") }),
        });
        var encounter = await CreateAsync(hapi, new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = "in-progress",
            ["class"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "AMB",
                }),
            }),
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
        });
        var patientRef = new JsonObject { ["reference"] = $"Patient/{patient}" };
        var encounterRef = $"Encounter/{encounter}";
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        JsonObject Coding(string system, string code, string? display = null)
        {
            var c = new JsonObject { ["system"] = system, ["code"] = code };
            if (display is not null) { c["display"] = display; }
            return new JsonObject { ["coding"] = new JsonArray(c) };
        }
        JsonObject Post(string urn, JsonObject resource) => new()
        {
            ["fullUrl"] = urn,
            ["resource"] = resource,
            ["request"] = new JsonObject { ["method"] = "POST", ["url"] = (string)resource["resourceType"]! },
        };
        JsonObject SocialHistory(string loinc, string label, string valueKey, JsonNode value) => new()
        {
            ["resourceType"] = "Observation",
            ["status"] = "final",
            ["category"] = new JsonArray(Coding("http://terminology.hl7.org/CodeSystem/observation-category", "social-history")),
            ["code"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject { ["system"] = "http://loinc.org", ["code"] = loinc, ["display"] = label }),
                ["text"] = label,
            },
            ["subject"] = patientRef.DeepClone(),
            ["encounter"] = new JsonObject { ["reference"] = encounterRef },
            ["effectiveDateTime"] = now,
            ["issued"] = now,
            [valueKey] = value,
        };

        var procedureUrn = $"urn:uuid:{Guid.NewGuid()}";
        var familyUrn = $"urn:uuid:{Guid.NewGuid()}";
        var immunizationUrn = $"urn:uuid:{Guid.NewGuid()}";
        var immunization2Urn = $"urn:uuid:{Guid.NewGuid()}";
        var deviceUrn = $"urn:uuid:{Guid.NewGuid()}";
        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(
                Post(procedureUrn, new JsonObject
                {
                    ["resourceType"] = "Procedure",
                    ["status"] = "completed",
                    ["code"] = new JsonObject { ["text"] = "Cesárea" },
                    ["subject"] = patientRef.DeepClone(),
                    ["occurrenceString"] = "2019",
                    ["recorded"] = now,
                    ["reportedBoolean"] = true,
                }),
                Post(familyUrn, new JsonObject
                {
                    ["resourceType"] = "FamilyMemberHistory",
                    ["status"] = "completed",
                    ["patient"] = patientRef.DeepClone(),
                    ["date"] = now,
                    ["relationship"] = Coding("http://terminology.hl7.org/CodeSystem/v3-RoleCode", "MTH", "Madre"),
                    ["condition"] = new JsonArray(new JsonObject
                    {
                        ["code"] = new JsonObject
                        {
                            ["coding"] = new JsonArray(new JsonObject
                            {
                                ["system"] = "http://id.who.int/icd11/mms", ["code"] = "5A11", ["display"] = "Diabetes tipo 2",
                            }),
                            ["text"] = "Diabetes tipo 2",
                        },
                    }),
                }),
                Post(immunizationUrn, new JsonObject
                {
                    ["resourceType"] = "Immunization",
                    ["status"] = "completed",
                    ["vaccineCode"] = Coding("https://imed.app/fhir/CodeSystem/vacunas-pai-hn", "TD", "Td (tétanos-difteria)"),
                    ["patient"] = patientRef.DeepClone(),
                    ["occurrenceDateTime"] = "2020-03-01",
                    ["primarySource"] = false,
                    ["protocolApplied"] = new JsonArray(new JsonObject { ["doseNumber"] = "Refuerzo 1" }),
                }),
                Post(immunization2Urn, new JsonObject
                {
                    ["resourceType"] = "Immunization",
                    ["status"] = "completed",
                    ["vaccineCode"] = Coding("https://imed.app/fhir/CodeSystem/vacunas-pai-hn", "BCG", "BCG"),
                    ["patient"] = patientRef.DeepClone(),
                    ["occurrenceString"] = "al nacer",
                    ["primarySource"] = false,
                    ["protocolApplied"] = new JsonArray(new JsonObject { ["doseNumber"] = "Única" }),
                }),
                Post(deviceUrn, new JsonObject
                {
                    ["resourceType"] = "DeviceUsage",
                    ["status"] = "active",
                    ["patient"] = patientRef.DeepClone(),
                    ["device"] = new JsonObject { ["concept"] = new JsonObject { ["text"] = "DIU" } },
                    ["context"] = new JsonObject { ["reference"] = encounterRef },
                    ["timingPeriod"] = new JsonObject { ["start"] = "2022-05-01" },
                    ["dateAsserted"] = now,
                }),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("72166-2", "Tabaco", "valueCodeableConcept", new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = "http://snomed.info/sct", ["code"] = "8517006", ["display"] = "Exfumador",
                    }),
                    ["text"] = "Exfumador",
                })),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("11331-6", "Alcohol", "valueCodeableConcept",
                    new JsonObject { ["text"] = "Ocasional" })),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("89574-8", "Actividad física", "valueQuantity", new JsonObject
                {
                    ["value"] = 150, ["unit"] = "min/semana", ["system"] = "http://unitsofmeasure.org", ["code"] = "min/wk",
                })),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("11996-6", "Gestas", "valueInteger", JsonValue.Create(2))),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("8665-2", "FUM", "valueDateTime", JsonValue.Create("2026-09-01"))),
                Post($"urn:uuid:{Guid.NewGuid()}", SocialHistory("42798-9", "Menarquia", "valueQuantity", new JsonObject
                {
                    ["value"] = 12, ["unit"] = "años", ["system"] = "http://unitsofmeasure.org", ["code"] = "a",
                })),
                Post($"urn:uuid:{Guid.NewGuid()}", new JsonObject
                {
                    ["resourceType"] = "Provenance",
                    ["target"] = new JsonArray(
                        new JsonObject { ["reference"] = procedureUrn },
                        new JsonObject { ["reference"] = familyUrn },
                        new JsonObject { ["reference"] = immunizationUrn },
                        new JsonObject { ["reference"] = deviceUrn }),
                    ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                    ["recorded"] = now,
                    ["agent"] = new JsonArray(new JsonObject { ["who"] = patientRef.DeepClone() }),
                })),
        };

        var results = await hapi.ExecuteTransactionAsync(bundle, Partition);
        results.Should().HaveCount(12);

        // §3.6: la consulta cerrada encuentra su constancia por encuentro, con los destinos ya
        // resueltos de urn:uuid a Tipo/id.
        var provenances = await hapi.SearchResourcesAsync("Provenance", $"encounter={encounterRef}", Partition);
        provenances.Should().ContainSingle();
        var targets = provenances[0]["target"]!.AsArray().Select(t => (string?)t!["reference"]).ToList();
        targets.Should().OnlyContain(t => t != null && !t.StartsWith("urn:"));
        Console.WriteLine($"[Smoke] destinos de la Provenance: {string.Join(", ", targets)}");

        // Las búsquedas del panel (history-records.service.ts).
        (await hapi.SearchResourcesAsync("Procedure", $"subject=Patient/{patient}&_count=100", Partition)).Should().HaveCount(1);
        (await hapi.SearchResourcesAsync("FamilyMemberHistory", $"patient=Patient/{patient}&_count=100", Partition)).Should().HaveCount(1);
        (await hapi.SearchResourcesAsync("Immunization", $"patient=Patient/{patient}&_count=100", Partition)).Should().HaveCount(2);
        (await hapi.SearchResourcesAsync("DeviceUsage", $"patient=Patient/{patient}&_count=100", Partition)).Should().HaveCount(1);
        (await hapi.SearchResourcesAsync("Observation",
            $"subject=Patient/{patient}&category=social-history&_sort=-date&_count=200", Partition)).Should().HaveCount(6);

        // Retirar el dispositivo: PUT con ifMatch, como lo arma deviceAdapter.end.
        var device = (await hapi.SearchResourcesAsync("DeviceUsage", $"patient=Patient/{patient}", Partition)).Single();
        var ended = (JsonObject)device.DeepClone();
        ended.Remove("meta");
        ended["status"] = "completed";
        ended["timingPeriod"] = new JsonObject { ["start"] = "2022-05-01", ["end"] = now };
        await hapi.ExecuteTransactionAsync(new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = new JsonArray(Put(ended, $"DeviceUsage/{device["id"]}", (string?)device["meta"]!["versionId"])),
        }, Partition);
        var reread = await hapi.ReadResourceAsync("DeviceUsage", (string)device["id"]!, Partition);
        ((string?)reread!["status"]).Should().Be("completed");
    }

    private static JsonObject Put(JsonObject resource, string url, string? versionId) => new()
    {
        ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
        ["resource"] = resource,
        ["request"] = new JsonObject { ["method"] = "PUT", ["url"] = url, ["ifMatch"] = $"W/\"{versionId}\"" },
    };
}
