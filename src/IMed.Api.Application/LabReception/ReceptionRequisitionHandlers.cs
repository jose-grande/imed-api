using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Resuelve una visita (encounter) como una requisición: carga todas sus órdenes
/// de laboratorio, las enruta y las agrupa por **tipo de muestra a recolectar**
/// (LIS-RD-02/04). Cada grupo = una muestra física que cubre N órdenes (N:M).
/// </summary>
public sealed class ResolveRequisitionHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops)
{
    public async Task<RequisitionView?> HandleAsync(Guid orgId, string encounterId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = await hapi.SearchLabOrdersByEncounterAsync(encounterId, partition, ct);
        // F2: solo se resuelven las órdenes ingresadas (check-in del paciente).
        var checkedIn = await ops.GetCheckedInRequisitionsAsync(orgId, ct);
        orders = orders.Where(o => o.Requisition is not null && checkedIn.Contains(o.Requisition)).ToList();
        if (orders.Count == 0)
        {
            return null;
        }
        return await BuildViewAsync(orgId, encounterId, orders, ct);
    }

    /// <summary>Resuelve una orden específica por su número (requisition) — buscador de recepción (F2).</summary>
    public async Task<RequisitionView?> HandleByOrderAsync(Guid orgId, string requisition, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = await hapi.SearchLabOrdersByRequisitionAsync(requisition, partition, ct);
        if (orders.Count == 0)
        {
            return null;
        }
        return await BuildViewAsync(orgId, requisition, orders, ct);
    }

    private async Task<RequisitionView?> BuildViewAsync(
        Guid orgId, string viewId, IReadOnlyList<LabOrderSnapshot> orders, CancellationToken ct)
    {
        var specimenTypes = (await catalog.ListSpecimenTypesAsync(orgId, ct)).ToDictionary(s => s.Id);
        var tubeTypes = (await catalog.ListTubeTypesAsync(orgId, ct)).ToDictionary(t => t.Id);

        var orderLists = new Dictionary<string, List<RequisitionOrder>>(StringComparer.Ordinal);
        var meta = new Dictionary<string, GroupMeta>(StringComparer.Ordinal);
        string? patientId = null;

        foreach (var o in orders)
        {
            patientId ??= o.PatientFhirId;
            var existing = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
            var intakeStatus = existing?.Status.ToString();

            // Expandir panel → pruebas (o prueba suelta) y agrupar por tipo de muestra.
            var tests = await LabRouting.ExpandOrderAsync(catalog, orgId, o.CatalogCode, ct);
            if (tests.Count == 0)
            {
                AddOrder("UNKNOWN", null, null, o, "SendOut", null, intakeStatus);
                continue;
            }

            var byType = new Dictionary<string, (SpecimenType? St, TubeType? Tt, bool AnyInHouse, int? Fasting)>(StringComparer.Ordinal);
            foreach (var (test, routing) in tests)
            {
                SpecimenType? st = test.SpecimenTypeId is Guid sid && specimenTypes.TryGetValue(sid, out var s) ? s : null;
                TubeType? tt = test.TubeTypeId is Guid tid && tubeTypes.TryGetValue(tid, out var t) ? t : null;
                var key = st?.Code ?? "UNKNOWN";
                var cur = byType.TryGetValue(key, out var v)
                    ? v
                    : (St: st, Tt: tt, AnyInHouse: false, Fasting: (int?)null);
                cur.St ??= st;
                cur.Tt ??= tt;
                cur.AnyInHouse = cur.AnyInHouse || routing == OrderRouting.InHouse;
                cur.Fasting = MaxNullable(cur.Fasting, test.FastingHours);
                byType[key] = cur;
            }

            foreach (var (key, v) in byType)
            {
                AddOrder(key, v.St, v.Tt, o, v.AnyInHouse ? "InHouse" : "SendOut", v.Fasting, intakeStatus);
            }
        }

        void AddOrder(string key, SpecimenType? st, TubeType? tt, LabOrderSnapshot o, string routing, int? fasting, string? intakeStatus)
        {
            if (!orderLists.TryGetValue(key, out var list))
            {
                list = [];
                orderLists[key] = list;
                meta[key] = new GroupMeta(
                    st?.Name ?? "Sin clasificar",
                    (st?.CollectionMode ?? CollectionMode.OnSite).ToString(),
                    tt?.Code, tt?.Name, tt?.Color);
            }
            list.Add(new RequisitionOrder(o.Id, o.LoincCode, o.CodeText, routing, fasting, intakeStatus));
        }

        static int? MaxNullable(int? a, int? b) =>
            a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

        var groups = orderLists.Select(kv => new RequisitionGroup(
            kv.Key, meta[kv.Key].Name, meta[kv.Key].CollectionMode,
            meta[kv.Key].TubeCode, meta[kv.Key].TubeName, meta[kv.Key].TubeColor, kv.Value)).ToList();

        return new RequisitionView(viewId, patientId ?? string.Empty, groups);
    }

    private sealed record GroupMeta(string Name, string CollectionMode, string? TubeCode, string? TubeName, string? TubeColor);
}

/// <summary>
/// Recibe una requisición: por cada grupo de tipo de muestra crea **una** muestra
/// (accession) que cubre todas las órdenes del grupo (`Specimen.request[]`, N:M),
/// con un `OrderIntake` por orden compartiendo la muestra. Soporta rechazo y
/// bloqueo por ayuno por grupo (LIS-RD-03/04/17).
/// </summary>
public sealed class ReceiveRequisitionHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    IUserPractitionerAssignmentRepository assignments,
    ILocationRepository locations,
    ResolveActiveLocationHandler activeLocation,
    IClinicClock clock,
    TimeProvider timeProvider,
    ILogger<ReceiveRequisitionHandler> logger)
{
    public async Task<IReadOnlyList<ReceiveGroupResult>> HandleAsync(ReceiveRequisitionCommand cmd, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(cmd.OrganizationId);
        var results = new List<ReceiveGroupResult>();

        // Practitioner del técnico/flebotomista → collector de la muestra + agente de Provenance.
        var assignment = await assignments.GetAsync(cmd.UserId, cmd.OrganizationId, ct);
        var collectorRef = string.IsNullOrWhiteSpace(assignment?.PractitionerFhirId) ? null : assignment!.PractitionerFhirId;

        // Sede de la recepción: de ella salen el correlativo y el segmento de sede del accession.
        var locationId = await activeLocation.HandleAsync(cmd.OrganizationId, cmd.UserId, null, ct);
        var locationCode = (await locations.GetAsync(cmd.OrganizationId, locationId, ct))?.Code;

        foreach (var group in cmd.Groups)
        {
            if (group.ServiceRequestIds.Count == 0)
            {
                continue;
            }

            // Snapshots de las órdenes del grupo (paciente, prioridad, prueba/ayuno).
            var snaps = new List<(LabOrderSnapshot Order, LabTestDefinition? Test)>();
            foreach (var srId in group.ServiceRequestIds)
            {
                var ord = await hapi.GetLabOrderAsync(srId, partition, ct);
                if (ord is null)
                {
                    continue;
                }
                var (_, test) = await LabRouting.ResolveAsync(catalog, cmd.OrganizationId, ord.CatalogCode, ct);
                snaps.Add((ord, test));
            }
            if (snaps.Count == 0)
            {
                continue;
            }

            // Rechazo del grupo (incidencia preanalítica).
            if (group.Reject)
            {
                var reason = string.IsNullOrWhiteSpace(group.RejectReason) ? "Muestra rechazada en recepción." : group.RejectReason!;
                foreach (var (ord, test) in snaps)
                {
                    var intake = NewIntake(cmd, ord, test, group);
                    intake.MarkRejected(reason, cmd.UserId, timeProvider);
                    await ops.AddIntakeAsync(intake, ct);
                    await ops.AddSpecimenEventAsync(
                        SpecimenEvent.Create(intake.Id, SpecimenEventType.Rejected, cmd.UserId, timeProvider: timeProvider), ct);
                }
                results.Add(new ReceiveGroupResult(group.SpecimenTypeCode, "Rejected", null, null, reason));
                continue;
            }

            // Precondición de ayuno no cumplida → bloquear el grupo (sin crear muestra).
            var fastingRequired = snaps.Any(s => s.Test?.FastingHours is not null);
            if (fastingRequired && group.FastingSatisfied == false)
            {
                foreach (var (ord, test) in snaps)
                {
                    var intake = NewIntake(cmd, ord, test, group);
                    intake.MarkBlocked("Precondición no cumplida: requiere ayuno.", cmd.UserId, timeProvider);
                    await ops.AddIntakeAsync(intake, ct);
                }
                results.Add(new ReceiveGroupResult(group.SpecimenTypeCode, "Blocked", null, null, "Ayuno no cumplido."));
                continue;
            }

            // Recepción: una muestra compartida por todas las órdenes del grupo (N:M).
            var now = timeProvider.GetUtcNow();
            // Día de la clínica para el correlativo; `now` sigue en UTC para el registro.
            var day = clock.Today();
            var seq = await ops.NextAccessionSeqAsync(cmd.OrganizationId, locationId, day, ct);
            var accession = LabAccession.Build(cmd.OrganizationId, locationCode, day, seq);

            var srIds = snaps.Select(s => s.Order.Id).ToList();
            var maxFasting = snaps
                .Select(s => s.Test?.FastingHours)
                .Where(h => h is not null)
                .Select(h => h!.Value)
                .DefaultIfEmpty(0)
                .Max();

            var specimenId = await hapi.CreateSpecimenAsync(new UpsertSpecimenRequest(
                PartitionName: partition,
                PatientFhirId: snaps[0].Order.PatientFhirId,
                ServiceRequestFhirId: srIds[0],
                Status: "available",
                TypeText: group.SpecimenTypeName ?? group.SpecimenTypeCode,
                AccessionNumber: accession,
                ReceivedTime: now.ToString("o"),
                CollectedDateTime: group.CollectedDateTime,
                FastingHours: maxFasting == 0 ? null : maxFasting,
                Note: null,
                AdditionalServiceRequestFhirIds: srIds.Skip(1).ToList(),
                CollectorPractitionerId: collectorRef,
                CollectionMethod: group.CollectionMethod,
                CollectionBodySite: group.CollectionBodySite), ct);

            foreach (var (ord, test) in snaps)
            {
                var intake = NewIntake(cmd, ord, test, group);
                intake.MarkReceived(specimenId, accession, cmd.UserId, group.FastingSatisfied, timeProvider);
                await ops.AddIntakeAsync(intake, ct);
                await ops.AddSpecimenEventAsync(
                    SpecimenEvent.Create(intake.Id, SpecimenEventType.Received, cmd.UserId, timeProvider: timeProvider), ct);
            }

            await TryProvenanceAsync(partition, collectorRef, specimenId, now, ct);
            results.Add(new ReceiveGroupResult(group.SpecimenTypeCode, "Received", accession, specimenId, null));
        }

        return results;
    }

    private OrderIntake NewIntake(ReceiveRequisitionCommand cmd, LabOrderSnapshot ord, LabTestDefinition? test, ReceiveGroup group)
    {
        var intake = OrderIntake.Create(
            cmd.OrganizationId, ord.Id, ord.PatientFhirId, OrderRouting.InHouse,
            LabRouting.ParsePriority(ord.Priority), test?.FastingHours is not null, timeProvider: timeProvider);
        intake.Classify(cmd.Requisition, group.SpecimenTypeCode, group.TubeCode, timeProvider);
        intake.DescribeExam(ord.LoincCode, ord.CodeText, timeProvider);
        return intake;
    }

    private async Task TryProvenanceAsync(string partition, string? collectorRef, string specimenId, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(collectorRef))
        {
            return;
        }
        try
        {
            await hapi.CreateProvenanceAsync(new CreateProvenanceRequest(
                partition, $"Specimen/{specimenId}", "CREATE", "Recepción de muestra",
                $"Practitioner/{collectorRef}", now.ToString("o")), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo registrar Provenance para Specimen {Id} (best-effort).", specimenId);
        }
    }
}
