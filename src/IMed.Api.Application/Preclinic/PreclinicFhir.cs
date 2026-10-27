using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace IMed.Api.Application.Preclinic;

/// <summary>
/// Traducción entre la preclínica y FHIR, sin I/O: leer las alergias existentes y armar la
/// transacción que escribe la preclínica. Vive aparte de los handlers para poder probar las reglas
/// —qué es un duplicado, cuándo se crea el encuentro, cuándo "sin alergias conocidas" deja de ser
/// cierto— sin levantar HAPI.
/// </summary>
public static class PreclinicFhir
{
    public const string LoincSystem = "http://loinc.org";
    public const string SnomedSystem = "http://snomed.info/sct";
    private const string UcumSystem = "http://unitsofmeasure.org";
    private const string ObservationCategorySystem = "http://terminology.hl7.org/CodeSystem/observation-category";
    private const string AllergyClinicalSystem = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical";
    private const string AllergyVerificationSystem = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification";
    private const string ActCodeSystem = "http://terminology.hl7.org/CodeSystem/v3-ActCode";
    private const string ParticipantFunctionSystem = "http://terminology.hl7.org/CodeSystem/provenance-participant-type";
    public const string AtcSystem = "http://www.whocc.no/atc";
    private const string AdherenceSystem = "http://hl7.org/fhir/CodeSystem/medication-statement-adherence";

    /// <summary>Adherencias que significan "ya no lo toma": el registro deja de ser medicación vigente.</summary>
    private static readonly HashSet<string> NotTakingAdherence = new(StringComparer.Ordinal)
    {
        "stopped", "stopped-as-directed", "stopped-not-as-directed", "not-taking",
    };

    /// <summary>SNOMED "No known allergy": la forma en que otros sistemas leen "sin alergias conocidas".</summary>
    public const string NoKnownAllergyCode = "716186003";

    /// <summary>
    /// Negaciones SNOMED: afirman la <i>ausencia</i> de alergias. No son alergias y no cuentan como
    /// tales al mostrar ni al buscar duplicados.
    /// </summary>
    private static readonly HashSet<string> NegationCodes = new(StringComparer.Ordinal)
    {
        NoKnownAllergyCode,
        "409137002", // No known drug allergy
        "429625007", // No known food allergy
        "428607008", // No known environmental allergy
    };

    /// <summary>Estados de la cita en los que el paciente está en la fila de preclínica.</summary>
    public static readonly IReadOnlySet<string> QueueStatuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "arrived", "checked-in",
    };

    /// <summary>
    /// Signos vitales de la preclínica: el mismo subconjunto LOINC de la Épica J (J-D4). Los rangos
    /// no son clínicos sino de cordura: atrapan el peso cargado en gramos o la temperatura en °F.
    /// </summary>
    public static readonly IReadOnlyList<VitalSpec> Vitals =
    [
        new("weightKg", "29463-7", "Peso corporal", "kg", "kg", 0.2m, 400m, v => v.WeightKg),
        new("heightCm", "8302-2", "Talla", "cm", "cm", 20m, 250m, v => v.HeightCm),
        new("systolicBp", "8480-6", "Presión arterial sistólica", "mmHg", "mm[Hg]", 40m, 300m, v => v.SystolicBp),
        new("diastolicBp", "8462-4", "Presión arterial diastólica", "mmHg", "mm[Hg]", 20m, 200m, v => v.DiastolicBp),
        new("temperatureC", "8310-5", "Temperatura corporal", "°C", "Cel", 30m, 45m, v => v.TemperatureC),
        new("heartRate", "8867-4", "Frecuencia cardíaca", "/min", "/min", 20m, 300m, v => v.HeartRate),
    ];

    public sealed record VitalSpec(
        string Field, string Loinc, string Display, string Unit, string Ucum,
        decimal Min, decimal Max, Func<PreclinicVitalsInput, decimal?> Read);

    /// <summary>Una alergia ya registrada, reducida a lo que importa para mostrarla y deduplicar.</summary>
    public sealed record ExistingAllergy(
        string Id,
        IReadOnlyList<(string? System, string? Code)> Codings,
        string Display,
        string? Category,
        string? VerificationStatus,
        string? VersionId,
        JsonObject Resource)
    {
        public bool IsNegation => Codings.Any(c => c.System == SnomedSystem && c.Code is not null && NegationCodes.Contains(c.Code));
        public bool IsNoKnownAllergy => Codings.Any(c => c.System == SnomedSystem && c.Code == NoKnownAllergyCode);
    }

    /// <summary>Medicación habitual vigente, reducida a lo que importa para mostrar y deduplicar.</summary>
    public sealed record ExistingMedication(string Id, string? AtcCode, string Display, string? DosageText, string Status);

    public sealed record PreclinicPlan(
        JsonObject Bundle,
        bool CreatesEncounter,
        int ObservationCount,
        int AllergyCount,
        int MedicationCount,
        IReadOnlyList<string> SkippedDuplicates,
        bool RecordsNoKnownAllergies,
        bool ChecksInAppointment);

    // ---- Lectura -------------------------------------------------------------------------------

    public static string? PatientIdOf(JsonObject appointment)
    {
        var subject = (string?)appointment["subject"]?["reference"];
        if (subject?.StartsWith("Patient/", StringComparison.Ordinal) == true)
        {
            return subject["Patient/".Length..];
        }
        if (appointment["participant"] is JsonArray participants)
        {
            foreach (var p in participants)
            {
                var actor = (string?)p?["actor"]?["reference"];
                if (actor?.StartsWith("Patient/", StringComparison.Ordinal) == true)
                {
                    return actor["Patient/".Length..];
                }
            }
        }
        return null;
    }

    public static ExistingAllergy? ParseAllergy(JsonObject resource)
    {
        var id = (string?)resource["id"];
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        var codings = new List<(string?, string?)>();
        string? display = null;
        if (resource["code"]?["coding"] is JsonArray arr)
        {
            foreach (var c in arr)
            {
                codings.Add(((string?)c?["system"], (string?)c?["code"]));
                display ??= (string?)c?["display"];
            }
        }
        var text = (string?)resource["code"]?["text"];
        var category = resource["category"] is JsonArray cats && cats.Count > 0 ? (string?)cats[0] : null;
        var verification = (string?)resource["verificationStatus"]?["coding"]?[0]?["code"];
        return new ExistingAllergy(
            id, codings, display ?? text ?? "—", category, verification,
            (string?)resource["meta"]?["versionId"], resource);
    }

    /// <summary>
    /// <c>MedicationStatement</c> vigente: registrado o en borrador, no cargado por error y sin una
    /// adherencia que diga que ya no lo toma. <c>null</c> si no es vigente.
    /// </summary>
    public static ExistingMedication? ParseMedication(JsonObject resource)
    {
        var id = (string?)resource["id"];
        var status = (string?)resource["status"];
        if (id is null || status is not ("recorded" or "draft"))
        {
            return null;
        }
        var adherence = (string?)resource["adherence"]?["code"]?["coding"]?[0]?["code"];
        if (adherence is not null && NotTakingAdherence.Contains(adherence))
        {
            return null;
        }
        var concept = resource["medication"]?["concept"];
        string? atc = null;
        string? display = null;
        if (concept?["coding"] is JsonArray codings)
        {
            foreach (var c in codings)
            {
                if ((string?)c?["system"] == AtcSystem) { atc ??= (string?)c?["code"]; }
                display ??= (string?)c?["display"];
            }
        }
        display ??= (string?)concept?["text"] ?? "—";
        var dosage = (string?)resource["dosage"]?[0]?["text"];
        return new ExistingMedication(id, atc, display, dosage, status);
    }

    public static PreclinicVitalDto? ParseVital(JsonObject resource)
    {
        var id = (string?)resource["id"];
        var loinc = resource["code"]?["coding"] is JsonArray codings
            ? codings.Select(c => c).FirstOrDefault(c => (string?)c?["system"] == LoincSystem)?["code"]
            : null;
        var valueNode = resource["valueQuantity"]?["value"];
        if (id is null || loinc is null || valueNode is null)
        {
            return null;
        }
        var effective = DateTimeOffset.TryParse(
            (string?)resource["effectiveDateTime"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : (DateTimeOffset?)null;
        return new PreclinicVitalDto(
            id, (string)loinc!, valueNode.GetValue<decimal>(), (string?)resource["valueQuantity"]?["unit"], effective);
    }

    /// <summary>
    /// Forma comparable de un texto libre: sin mayúsculas, tildes ni espacios de más. "Penicilina ",
    /// "penicilina" y "PENICILÍNA" son la misma alergia escrita por tres personas.
    /// </summary>
    public static string Normalize(string text)
    {
        var decomposed = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var lastWasSpace = false;
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace) { sb.Append(' '); }
                lastWasSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(ch));
            lastWasSpace = false;
        }
        return sb.ToString().Trim();
    }

    // ---- Escritura -----------------------------------------------------------------------------

    /// <summary>
    /// Arma la transacción de la preclínica.
    ///
    /// <para><b>El encuentro (P-D2).</b> Si la cita no tiene uno abierto, se crea en <c>planned</c>
    /// con creación condicional (<c>ifNoneExist</c>): dos envíos simultáneos de la misma preclínica
    /// no pueden dejar dos encuentros para una cita. Las Observations nacen apuntando a él, que es lo
    /// que hace que la consulta muestre los signos vitales <i>de esta visita</i> y no los de
    /// cualquier visita de las últimas 24 horas.</para>
    ///
    /// <para><b>Las alergias.</b> Lo ya registrado no se vuelve a crear: mismo código SNOMED o mismo
    /// texto normalizado es la misma alergia. Lo que agrega la auxiliar entra <i>sin confirmar</i>;
    /// la confirma el médico.</para>
    /// </summary>
    public static PreclinicPlan Build(
        JsonObject appointment,
        string patientId,
        string? encounterId,
        IReadOnlyList<ExistingAllergy> existing,
        SubmitPreclinicCommand cmd,
        DateTimeOffset now,
        IReadOnlyList<ExistingMedication>? existingMedications = null)
    {
        var appointmentId = (string?)appointment["id"]
            ?? throw new ArgumentException("La cita no tiene id.");
        var nowText = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var patientRef = $"Patient/{patientId}";
        var performerRef = string.IsNullOrWhiteSpace(cmd.PerformerPractitionerId)
            ? null : $"Practitioner/{cmd.PerformerPractitionerId}";
        var entries = new JsonArray();

        // 1. Encuentro.
        string encounterRef;
        var createsEncounter = encounterId is null;
        if (createsEncounter)
        {
            encounterRef = $"urn:uuid:{Guid.NewGuid()}";
            entries.Add(new JsonObject
            {
                ["fullUrl"] = encounterRef,
                ["resource"] = new JsonObject
                {
                    ["resourceType"] = "Encounter",
                    ["status"] = "planned",
                    ["class"] = new JsonArray(new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = ActCodeSystem, ["code"] = "AMB", ["display"] = "ambulatory",
                        }),
                    }),
                    ["subject"] = new JsonObject { ["reference"] = patientRef },
                    ["appointment"] = new JsonArray(new JsonObject { ["reference"] = $"Appointment/{appointmentId}" }),
                },
                ["request"] = new JsonObject
                {
                    ["method"] = "POST",
                    ["url"] = "Encounter",
                    ["ifNoneExist"] = $"appointment=Appointment/{appointmentId}&status=planned,in-progress",
                },
            });

            // El encuentro ocurre en la sede de la cita (FAC-9). Las citas anteriores a FAC-9 no la
            // tienen; ahí el encuentro queda sin sede, como la cita. Se agrega solo si existe:
            // FHIR rechaza `"location": null`.
            if (SedeOf(appointment) is { } sede)
            {
                entries[entries.Count - 1]!["resource"]!["location"] = new JsonArray(
                    new JsonObject { ["location"] = new JsonObject { ["reference"] = sede } });
            }
        }
        else
        {
            encounterRef = $"Encounter/{encounterId}";
        }

        // 2. Signos vitales.
        var observations = 0;
        if (cmd.Vitals is not null)
        {
            foreach (var spec in Vitals)
            {
                var value = spec.Read(cmd.Vitals);
                if (value is null)
                {
                    continue;
                }
                if (value < spec.Min || value > spec.Max)
                {
                    throw new ArgumentException(
                        $"{spec.Display}: {value} {spec.Unit} está fuera del rango admitido ({spec.Min}–{spec.Max}).");
                }
                var obs = new JsonObject
                {
                    ["resourceType"] = "Observation",
                    ["status"] = "final",
                    ["category"] = new JsonArray(new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = ObservationCategorySystem, ["code"] = "vital-signs",
                        }),
                    }),
                    ["code"] = new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject
                        {
                            ["system"] = LoincSystem, ["code"] = spec.Loinc, ["display"] = spec.Display,
                        }),
                        ["text"] = spec.Display,
                    },
                    ["subject"] = new JsonObject { ["reference"] = patientRef },
                    ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                    ["effectiveDateTime"] = nowText,
                    ["issued"] = nowText,
                    ["valueQuantity"] = new JsonObject
                    {
                        ["value"] = value.Value, ["unit"] = spec.Unit, ["system"] = UcumSystem, ["code"] = spec.Ucum,
                    },
                };
                if (performerRef is not null)
                {
                    obs["performer"] = new JsonArray(new JsonObject { ["reference"] = performerRef });
                }
                entries.Add(PostEntry(obs, "Observation"));
                observations++;
            }
        }

        // 3. Alergias nuevas, sin duplicar lo existente ni repetir dentro del mismo envío.
        var real = existing.Where(a => !a.IsNegation).ToList();
        var knownCodes = new HashSet<string>(
            real.SelectMany(a => a.Codings).Where(c => c.System == SnomedSystem && c.Code is not null).Select(c => c.Code!),
            StringComparer.Ordinal);
        var knownTexts = new HashSet<string>(real.Select(a => Normalize(a.Display)), StringComparer.Ordinal);
        var skipped = new List<string>();
        var allergies = 0;

        foreach (var input in cmd.CatalogAllergies ?? [])
        {
            if (string.IsNullOrWhiteSpace(input.Code))
            {
                continue;
            }
            var display = string.IsNullOrWhiteSpace(input.Display) ? input.Code : input.Display.Trim();
            if (!knownCodes.Add(input.Code) || !knownTexts.Add(Normalize(display)))
            {
                skipped.Add(display);
                continue;
            }
            entries.Add(PostEntry(BuildAllergy(patientRef, performerRef, nowText,
                code: input.Code, display: display, category: MapCategory(input.Category)), "AllergyIntolerance"));
            allergies++;
        }
        foreach (var raw in cmd.FreeTextAllergies ?? [])
        {
            var text = raw?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            if (!knownTexts.Add(Normalize(text)))
            {
                skipped.Add(text);
                continue;
            }
            entries.Add(PostEntry(BuildAllergy(patientRef, performerRef, nowText,
                code: null, display: text, category: null), "AllergyIntolerance"));
            allergies++;
        }

        // 4. "Sin alergias conocidas".
        var activeNoKnown = existing.Where(a => a.IsNoKnownAllergy).ToList();
        var recordsNoKnown = false;
        if (cmd.NoKnownAllergies)
        {
            if (real.Count > 0 || allergies > 0)
            {
                throw new ArgumentException(
                    "No se puede registrar \"sin alergias conocidas\": el paciente tiene alergias activas.");
            }
            if (activeNoKnown.Count == 0)
            {
                entries.Add(PostEntry(BuildAllergy(patientRef, performerRef, nowText,
                    code: NoKnownAllergyCode, display: "Sin alergias conocidas", category: null), "AllergyIntolerance"));
                recordsNoKnown = true;
            }
        }
        else if (allergies > 0)
        {
            // Apareció una alergia: la afirmación de que no había ninguna dejó de ser cierta. Se
            // inactiva en vez de borrarse — que alguna vez se dijo es parte de la historia.
            foreach (var nka in activeNoKnown)
            {
                var updated = (JsonObject)nka.Resource.DeepClone();
                updated["clinicalStatus"] = Concept(AllergyClinicalSystem, "inactive");
                entries.Add(PutEntry(updated, $"AllergyIntolerance/{nka.Id}", nka.VersionId));
            }
        }

        // 4b. Medicación que el paciente dice tomar (P-2). Entra en borrador: la concilia el médico.
        //     Lo ya registrado no se repite: mismo código ATC o mismo texto normalizado.
        var knownAtc = new HashSet<string>(
            (existingMedications ?? []).Where(m => m.AtcCode is not null).Select(m => m.AtcCode!), StringComparer.Ordinal);
        var knownMedTexts = new HashSet<string>(
            (existingMedications ?? []).Select(m => Normalize(m.Display)), StringComparer.Ordinal);
        var medications = 0;
        foreach (var input in cmd.Medications ?? [])
        {
            var display = input.Display?.Trim();
            if (string.IsNullOrEmpty(display))
            {
                continue;
            }
            var atc = string.IsNullOrWhiteSpace(input.AtcCode) ? null : input.AtcCode.Trim();
            if ((atc is not null && !knownAtc.Add(atc)) || !knownMedTexts.Add(Normalize(display)))
            {
                skipped.Add(display);
                continue;
            }
            var concept = new JsonObject { ["text"] = display };
            if (atc is not null)
            {
                concept["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = AtcSystem, ["code"] = atc, ["display"] = display,
                });
            }
            var statement = new JsonObject
            {
                ["resourceType"] = "MedicationStatement",
                // Declarado por el paciente ante la auxiliar: borrador hasta que el médico lo concilie.
                ["status"] = "draft",
                ["medication"] = new JsonObject { ["concept"] = concept },
                ["subject"] = new JsonObject { ["reference"] = patientRef },
                ["encounter"] = new JsonObject { ["reference"] = encounterRef },
                ["dateAsserted"] = nowText,
                ["informationSource"] = new JsonArray(new JsonObject { ["reference"] = patientRef }),
                ["adherence"] = new JsonObject
                {
                    ["code"] = new JsonObject
                    {
                        ["coding"] = new JsonArray(new JsonObject { ["system"] = AdherenceSystem, ["code"] = "taking" }),
                    },
                },
            };
            if (!string.IsNullOrWhiteSpace(input.DosageText))
            {
                statement["dosage"] = new JsonArray(new JsonObject { ["text"] = input.DosageText.Trim() });
            }
            entries.Add(PostEntry(statement, "MedicationStatement"));
            medications++;
        }

        // 5. La cita pasa a checked-in (el paciente ya pasó por preclínica).
        var checksIn = (string?)appointment["status"] != "checked-in";
        if (checksIn)
        {
            var updated = (JsonObject)appointment.DeepClone();
            updated["status"] = "checked-in";
            entries.Add(PutEntry(updated, $"Appointment/{appointmentId}", (string?)appointment["meta"]?["versionId"]));
        }

        if (observations == 0 && allergies == 0 && medications == 0 && !recordsNoKnown)
        {
            throw new ArgumentException("La preclínica no trae ningún signo vital, alergia ni medicamento que registrar.");
        }

        var bundle = new JsonObject
        {
            ["resourceType"] = "Bundle",
            ["type"] = "transaction",
            ["entry"] = entries,
        };
        return new PreclinicPlan(bundle, createsEncounter, observations, allergies, medications, skipped, recordsNoKnown, checksIn);
    }

    private static JsonObject BuildAllergy(
        string patientRef, string? recorderRef, string nowText, string? code, string display, string? category)
    {
        var codeNode = new JsonObject { ["text"] = display };
        if (code is not null)
        {
            codeNode["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = SnomedSystem, ["code"] = code, ["display"] = display,
            });
        }
        var allergy = new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = Concept(AllergyClinicalSystem, "active"),
            // Declarado en preclínica: lo confirma el médico (§3.5 de la épica).
            ["verificationStatus"] = Concept(AllergyVerificationSystem, "unconfirmed"),
            ["code"] = codeNode,
            ["patient"] = new JsonObject { ["reference"] = patientRef },
            ["recordedDate"] = nowText,
        };
        if (category is not null)
        {
            allergy["category"] = new JsonArray(category);
        }
        if (recorderRef is not null)
        {
            // R5: quien registra va en `participant` con función `author`. `recorder` es de R4 y
            // HAPI lo rechaza con 422 ("Unrecognized property 'recorder'").
            allergy["participant"] = new JsonArray(new JsonObject
            {
                ["function"] = Concept(ParticipantFunctionSystem, "author"),
                ["actor"] = new JsonObject { ["reference"] = recorderRef },
            });
        }
        return allergy;
    }

    /// <summary>Categorías del catálogo (J1) → <c>AllergyIntolerance.category</c>. "other" no tiene equivalente.</summary>
    private static string? MapCategory(string? category) => category switch
    {
        "food" or "medication" or "environment" or "biologic" => category,
        _ => null,
    };

    private static JsonObject Concept(string system, string code) => new()
    {
        ["coding"] = new JsonArray(new JsonObject { ["system"] = system, ["code"] = code }),
    };

    private static JsonObject PostEntry(JsonObject resource, string type) => new()
    {
        ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
        ["resource"] = resource,
        ["request"] = new JsonObject { ["method"] = "POST", ["url"] = type },
    };

    private static JsonObject PutEntry(JsonObject resource, string url, string? versionId)
    {
        // `meta.versionId`/`lastUpdated` los asigna el servidor; mandarlos solo confunde. Si no queda
        // nada más (tags, perfiles), se quita `meta` entero: HAPI rechaza un objeto vacío con 422.
        if (resource["meta"] is JsonObject meta)
        {
            meta.Remove("versionId");
            meta.Remove("lastUpdated");
            meta.Remove("source");
            if (meta.Count == 0)
            {
                resource.Remove("meta");
            }
        }
        var request = new JsonObject { ["method"] = "PUT", ["url"] = url };
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            // Si otra persona cambió el recurso desde que se leyó, HAPI responde 412 y no se
            // escribe nada: mejor eso que pisar su cambio sin que nadie se entere.
            request["ifMatch"] = $"W/\"{versionId}\"";
        }
        return new JsonObject
        {
            ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
            ["resource"] = resource,
            ["request"] = request,
        };
    }

    /// <summary>Referencia a la sede de la cita (<c>participant.actor</c> de tipo Location), o null.</summary>
    internal static string? SedeOf(JsonObject appointment) =>
        (appointment["participant"] as JsonArray)?
            .Select(p => (string?)p?["actor"]?["reference"])
            .FirstOrDefault(r => r is not null && r.StartsWith("Location/", StringComparison.Ordinal));
}
