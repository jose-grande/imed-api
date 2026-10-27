using System.Globalization;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Preclinic;

namespace IMed.Api.Application.Allergies;

/// <summary>Un grupo de alergias que son la misma: cuál se conserva y cuáles se marcan como error.</summary>
public sealed record AllergyDuplicateGroup(
    string PatientFhirId,
    string Display,
    string KeptId,
    IReadOnlyList<string> DuplicateIds);

public sealed record AllergyDedupeReport(
    bool Applied,
    int AllergiesScanned,
    int DuplicatesFound,
    IReadOnlyList<AllergyDuplicateGroup> Groups);

/// <summary>
/// Limpieza de las alergias duplicadas que dejó la preclínica antes de la Épica P-1: como no veía
/// las existentes, creaba otra en cada visita. P-1 cierra la causa; esto repara lo ya escrito.
///
/// <para><b>Qué es un duplicado.</b> Mismo paciente y misma alergia: mismo código SNOMED, o —sin
/// código— mismo texto normalizado. Es la misma regla con la que P-1 evita crearlos.</para>
///
/// <para><b>Cuál se conserva.</b> La confirmada si hay alguna (la afirmó un médico) y, entre las
/// candidatas, la más antigua: es la que lleva la fecha real del primer registro.</para>
///
/// <para><b>Nada se borra.</b> Las demás pasan a <c>entered-in-error</c>: dejan de mostrarse y de
/// contar, pero siguen auditables. Cada escritura lleva la versión leída, así que si alguien tocó
/// una alergia entre el reporte y la aplicación, esa transacción falla y no se pisa nada.</para>
///
/// <para>Corre por organización (la del token) y por defecto solo reporta: aplicar es explícito.</para>
/// </summary>
public sealed class AllergyDedupeHandler(IHapiFhirClient hapi)
{
    private const int PageSize = 200;
    private const int MaxPages = 100;
    private const int MaxEntriesPerTransaction = 100;

    public async Task<AllergyDedupeReport> HandleAsync(Guid organizationId, bool apply, CancellationToken ct = default)
    {
        var partition = $"org-{organizationId}";
        var all = new List<JsonObject>();
        for (var page = 0; page < MaxPages; page++)
        {
            var batch = await hapi.SearchResourcesAsync(
                "AllergyIntolerance",
                $"clinical-status=active&_count={PageSize}&_offset={page * PageSize}",
                partition, ct);
            all.AddRange(batch);
            if (batch.Count < PageSize)
            {
                break;
            }
        }

        var candidates = all
            .Select(r => (Resource: r, Allergy: PreclinicFhir.ParseAllergy(r), Patient: PatientOf(r)))
            .Where(x => x.Allergy is not null && x.Patient is not null && x.Allergy.VerificationStatus != "entered-in-error")
            .ToList();

        var groups = new List<(AllergyDuplicateGroup Report, List<JsonObject> Duplicates)>();
        foreach (var group in candidates.GroupBy(x => (x.Patient!, KeyOf(x.Allergy!))))
        {
            if (group.Count() < 2)
            {
                continue;
            }
            var ordered = group
                .OrderBy(x => x.Allergy!.VerificationStatus == "confirmed" ? 0 : 1)
                .ThenBy(x => RecordedAt(x.Resource))
                .ThenBy(x => x.Allergy!.Id, StringComparer.Ordinal)
                .ToList();
            var kept = ordered[0];
            var duplicates = ordered.Skip(1).ToList();
            groups.Add((
                new AllergyDuplicateGroup(group.Key.Item1, kept.Allergy!.Display, kept.Allergy.Id,
                    duplicates.Select(d => d.Allergy!.Id).ToList()),
                duplicates.Select(d => d.Resource).ToList()));
        }

        if (apply)
        {
            var toMark = groups.SelectMany(g => g.Duplicates).ToList();
            foreach (var chunk in toMark.Chunk(MaxEntriesPerTransaction))
            {
                await hapi.ExecuteTransactionAsync(BuildTransaction(chunk), partition, ct);
            }
        }

        return new AllergyDedupeReport(
            apply,
            candidates.Count,
            groups.Sum(g => g.Report.DuplicateIds.Count),
            groups.Select(g => g.Report).ToList());
    }

    private static JsonObject BuildTransaction(IEnumerable<JsonObject> duplicates)
    {
        var entries = new JsonArray();
        foreach (var original in duplicates)
        {
            var resource = (JsonObject)original.DeepClone();
            var id = (string)resource["id"]!;
            var version = (string?)resource["meta"]?["versionId"];
            resource.Remove("meta");
            // R5: con entered-in-error, clinicalStatus no puede estar presente.
            resource.Remove("clinicalStatus");
            resource["verificationStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-verification",
                    ["code"] = "entered-in-error",
                }),
            };
            var request = new JsonObject { ["method"] = "PUT", ["url"] = $"AllergyIntolerance/{id}" };
            if (version is not null)
            {
                request["ifMatch"] = $"W/\"{version}\"";
            }
            entries.Add(new JsonObject
            {
                ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
                ["resource"] = resource,
                ["request"] = request,
            });
        }
        return new JsonObject { ["resourceType"] = "Bundle", ["type"] = "transaction", ["entry"] = entries };
    }

    private static string? PatientOf(JsonObject r)
    {
        var reference = (string?)r["patient"]?["reference"];
        return reference?.StartsWith("Patient/", StringComparison.Ordinal) == true ? reference["Patient/".Length..] : null;
    }

    private static string KeyOf(PreclinicFhir.ExistingAllergy a)
    {
        var snomed = a.Codings.FirstOrDefault(c => c.System == PreclinicFhir.SnomedSystem && c.Code is not null);
        return snomed.Code is not null ? $"snomed:{snomed.Code}" : $"text:{PreclinicFhir.Normalize(a.Display)}";
    }

    private static DateTimeOffset RecordedAt(JsonObject r)
    {
        foreach (var candidate in new[] { (string?)r["recordedDate"], (string?)r["meta"]?["lastUpdated"] })
        {
            if (DateTimeOffset.TryParse(candidate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                return at;
            }
        }
        return DateTimeOffset.MaxValue;
    }
}
