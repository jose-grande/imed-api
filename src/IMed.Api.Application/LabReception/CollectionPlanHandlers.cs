using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Motor de plan de colección (R6-F2). Dada una requisición, expande sus órdenes a
/// pruebas, deriva por prueba el tipo de muestra/tubo/volumen del catálogo y
/// **consolida** las pruebas compatibles en contenedores físicos respetando la
/// capacidad del tubo (decisión UX §8.10). El resultado se persiste y lo consume
/// el workbench (R6-F5) antes de etiquetar (R6-F4).
/// </summary>
public sealed class ComputeCollectionPlanHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops, TimeProvider timeProvider)
{
    /// <summary>Volumen por defecto por prueba cuando el catálogo no lo define (mL).</summary>
    private const decimal DefaultPerTestMl = 0.5m;

    public async Task<CollectionPlanDto?> HandleAsync(Guid orgId, string requisition, Guid? userId, CancellationToken ct = default)
    {
        // Si ya hay un plan que avanzó (etiqueta/toma/recepción), no se recalcula:
        // recalcular borraría contenedores y su ciclo de vida (R6-F3/F4).
        var existing = await ops.GetCollectionPlanByRequisitionAsync(orgId, requisition, ct);
        if (existing is not null && existing.Containers.Any(c => c.Status != ContainerStatus.PlanCalculated))
        {
            return CollectionPlanDto.FromDomain(existing);
        }

        var partition = LabRouting.PartitionOf(orgId);
        var orders = await hapi.SearchLabOrdersByRequisitionAsync(requisition, partition, ct);
        if (orders.Count == 0)
        {
            return null;
        }

        var specimenTypes = (await catalog.ListSpecimenTypesAsync(orgId, ct)).ToDictionary(s => s.Id);
        var tubeTypes = (await catalog.ListTubeTypesAsync(orgId, ct)).ToDictionary(t => t.Id);

        // Resolver todas las pruebas de la requisición (expandiendo paneles) con su orden de origen.
        // Las órdenes que NO resuelven en el catálogo del tenant (copy-on-create, rev 53) se registran
        // como "no planificables" en vez de perderse en silencio: antes el fallback global las resolvía.
        var resolved = new List<ResolvedTest>();
        var unresolved = new List<LabOrderSnapshot>();
        string? patientId = null;
        string? encounterId = null;
        foreach (var o in orders)
        {
            patientId ??= o.PatientFhirId;
            encounterId ??= o.EncounterFhirId;
            var tests = await LabRouting.ExpandOrderAsync(catalog, orgId, o.LoincCode, ct);
            if (tests.Count == 0)
            {
                unresolved.Add(o);
                continue;
            }
            foreach (var (test, _) in tests)
            {
                SpecimenType? st = test.SpecimenTypeId is Guid sid && specimenTypes.TryGetValue(sid, out var s) ? s : null;
                TubeType? tt = test.TubeTypeId is Guid tid && tubeTypes.TryGetValue(tid, out var t) ? t : null;
                resolved.Add(new ResolvedTest(o.Id, test, st, tt));
            }
        }

        // Una prueba se mide una sola vez por requisición — dedupe por código (toma el primer origen).
        resolved = resolved
            .GroupBy(r => r.Test.Code, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        var plan = CollectionPlan.Create(orgId, requisition, patientId ?? string.Empty, encounterId, userId, timeProvider);
        BuildContainers(plan, resolved);
        foreach (var o in unresolved)
        {
            var display = o.CodeDisplay ?? o.CodeText ?? o.LoincCode ?? "(sin código)";
            plan.AddUnavailableItem(o.Id, o.LoincCode, display, "not-in-catalog");
        }

        await ops.UpsertCollectionPlanAsync(plan, ct);
        return CollectionPlanDto.FromDomain(plan);
    }

    /// <summary>
    /// Agrupa por (tipo de muestra, aditivo) y empaca pruebas en contenedores
    /// respetando la capacidad útil del tubo (capacidad − volumen muerto); abre un
    /// contenedor nuevo del mismo aditivo al superarla o si el aditivo difiere. Ordena
    /// los grupos por order-of-draw y asigna <c>Sequence</c>.
    /// </summary>
    private void BuildContainers(CollectionPlan plan, IReadOnlyList<ResolvedTest> resolved)
    {
        var sequence = 0;
        var groups = resolved
            .GroupBy(r => (Specimen: r.SpecimenType?.Code ?? "UNKNOWN", Tube: r.TubeType?.Code))
            .OrderBy(g => DrawOrder(g.Key.Tube))
            .ThenBy(g => g.Key.Specimen, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var sample = group.First();
            var st = sample.SpecimenType;
            var tt = sample.TubeType;
            var dead = tt?.DeadVolumeMl ?? 0m;
            decimal? usable = tt?.CapacityMl is decimal cap ? cap - dead : null;

            CollectionPlanContainer? current = null;
            foreach (var rt in group)
            {
                var vol = rt.Test.MinVolumeMl ?? rt.Test.AnalyzerMinVolumeMl ?? DefaultPerTestMl;
                // Volumen ya consumido por las pruebas del contenedor actual (sin contar el muerto).
                var consumed = current is null ? 0m : current.RequiredVolumeMl - dead;
                var exceeds = usable is decimal u && current is not null && consumed + vol > u;
                if (current is null || exceeds)
                {
                    current = plan.AddContainer(
                        ++sequence, st?.Code ?? "UNKNOWN", st?.Name,
                        tt?.Code, tt?.Name, tt?.Additive, dead, timeProvider);
                }
                current.AddItem(rt.ServiceRequestFhirId, rt.Test.Code, rt.Test.Name, vol);
            }
        }
    }

    /// <summary>Order of draw (orden de llenado de tubos). Menor = se extrae primero.</summary>
    private static int DrawOrder(string? tubeCode) => tubeCode switch
    {
        "CIT" => 1,   // citrato (celeste)
        "SST" => 2,   // suero/gel (dorado)
        "HEP" => 3,   // heparina (verde)
        "EDTA" => 4,  // EDTA (lila)
        _ => 9,       // sin tubo (orina/hisopado, etc.) al final
    };

    private sealed record ResolvedTest(
        string ServiceRequestFhirId, LabTestDefinition Test, SpecimenType? SpecimenType, TubeType? TubeType);
}

/// <summary>Lee el plan de colección persistido de una requisición (R6-F2).</summary>
public sealed class GetCollectionPlanHandler(ILabOperationsRepository ops)
{
    public async Task<CollectionPlanDto?> HandleAsync(Guid orgId, string requisition, CancellationToken ct = default)
    {
        var plan = await ops.GetCollectionPlanByRequisitionAsync(orgId, requisition, ct);
        return plan is null ? null : CollectionPlanDto.FromDomain(plan);
    }
}
