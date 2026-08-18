using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabReception;

/// <summary>Lógica compartida de enrutamiento de la orden (LIS-DEC-13).</summary>
internal static class LabRouting
{
    /// <summary>
    /// Resuelve el enrutamiento de una orden por su código —LOINC o el propio de la clínica—:
    /// in-house si la prueba está en el catálogo de la sede y ofrecida; send-out en caso contrario.
    ///
    /// <para>Acepta las dos formas porque la orden trae la que tenga: mirar solo LOINC deja fuera
    /// del catálogo a cualquier prueba que no lo tenga, y "fuera del catálogo" acá significa
    /// mandar al paciente a un laboratorio externo.</para>
    /// </summary>
    public static async Task<(OrderRouting Routing, LabTestDefinition? Test)> ResolveAsync(
        ILabCatalogRepository catalog, Guid orgId, string? codeOrLoinc, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(codeOrLoinc))
        {
            return (OrderRouting.SendOut, null);
        }
        var test = await catalog.GetTestByCodeOrLoincAsync(orgId, codeOrLoinc, ct);
        if (test is null || !test.Active)
        {
            return (OrderRouting.SendOut, null);
        }
        var offering = await catalog.GetOfferingAsync(orgId, test.Id, ct);
        var offered = offering?.Offered ?? true; // ausencia de fila = ofrecida (L1-D2)
        return (offered ? OrderRouting.InHouse : OrderRouting.SendOut, test);
    }

    /// <summary>
    /// Resuelve una **orden** —prueba suelta o panel— a lo que la recepción necesita saber de ella.
    ///
    /// <para><see cref="ResolveAsync"/> resuelve *una prueba*, y para una orden-panel no encuentra
    /// ninguna: devuelve send-out y sin datos. Eso hacía que la recepción refiriera a externo un
    /// panel que la clínica sí hace, y —más grave— que no viera el ayuno que exige alguna de sus
    /// pruebas.</para>
    /// </summary>
    public static async Task<OrderResolution> ResolveOrderAsync(
        ILabCatalogRepository catalog, Guid orgId, string? codeOrLoinc, CancellationToken ct)
    {
        var expanded = await ExpandOrderAsync(catalog, orgId, codeOrLoinc, ct);
        if (expanded.Count == 0)
        {
            return new OrderResolution(OrderRouting.SendOut, null, null, null, null);
        }

        var tests = expanded.Select(e => e.Test).ToList();
        var routing = expanded.Any(e => e.Routing == OrderRouting.InHouse)
            ? OrderRouting.InHouse
            : OrderRouting.SendOut;

        // El ayuno más estricto manda: si una sola prueba del panel exige 8 h, el paciente tiene
        // que venir con 8 h. Con el panel sin resolver esto quedaba en null y la precondición
        // no se preguntaba — se recibía la muestra igual.
        var fasting = tests.Max(t => t.FastingHours);

        // Tipo de muestra y tipo de colección, solo si todas las pruebas coinciden: un panel de
        // sangre y orina no tiene "el tipo", y estos caminos crean una sola muestra. Elegir uno
        // sería inventar; en null, la pantalla lo pide en vez de darlo por sabido.
        var specimenTypes = tests.Select(t => t.SpecimenTypeId).Distinct().ToList();
        var collectionTypes = tests.Select(t => t.CollectionType)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new OrderResolution(
            routing,
            fasting,
            collectionTypes.Count == 1 ? collectionTypes[0] : null,
            specimenTypes.Count == 1 ? specimenTypes[0] : null,
            tests.Count == 1 ? tests[0] : null);
    }

    public static LabRequestPriority ParsePriority(string? priority) =>
        Enum.TryParse<LabRequestPriority>(priority, ignoreCase: true, out var p) ? p : LabRequestPriority.Routine;

    public static string PartitionOf(Guid orgId) => $"org-{orgId}";

    /// <summary>
    /// Expande una orden a sus pruebas efectivas (R3, LIS-RD-01): si el código es
    /// un **panel** (por code local o LOINC), devuelve sus pruebas de la versión
    /// activa; si es una prueba suelta, la devuelve sola. Cada prueba con su
    /// enrutamiento in-house/send-out.
    /// </summary>
    public static async Task<IReadOnlyList<(LabTestDefinition Test, OrderRouting Routing)>> ExpandOrderAsync(
        ILabCatalogRepository catalog, Guid orgId, string? loincOrPanelCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loincOrPanelCode))
        {
            return Array.Empty<(LabTestDefinition, OrderRouting)>();
        }

        var panel = await catalog.GetPanelByCodeOrLoincAsync(orgId, loincOrPanelCode, ct);
        if (panel is not null)
        {
            var version = await catalog.GetActiveVersionAsync(panel.Id, ct);
            if (version is not null)
            {
                var items = await catalog.GetVersionItemsAsync(version.Id, ct);
                var tests = (await catalog.GetTestsByIdsAsync(items.Select(i => i.LabTestId).ToList(), ct))
                    .ToDictionary(t => t.Id);
                var result = new List<(LabTestDefinition, OrderRouting)>();
                foreach (var item in items.OrderBy(i => i.DisplayOrder))
                {
                    if (!tests.TryGetValue(item.LabTestId, out var t))
                    {
                        continue;
                    }
                    var offering = await catalog.GetOfferingAsync(orgId, t.Id, ct);
                    var offered = offering?.Offered ?? true;
                    result.Add((t, offered && t.Active ? OrderRouting.InHouse : OrderRouting.SendOut));
                }
                return result;
            }
        }

        var (routing, test) = await ResolveAsync(catalog, orgId, loincOrPanelCode, ct);
        return test is not null ? [(test, routing)] : Array.Empty<(LabTestDefinition, OrderRouting)>();
    }
}

/// <summary>
/// Lo que la recepción necesita de una orden, sea prueba suelta o panel.
/// <c>SingleTest</c> es la prueba solo cuando la orden es de una sola; para un panel es null
/// a propósito, porque ninguna de sus pruebas lo representa.
/// </summary>
internal sealed record OrderResolution(
    OrderRouting Routing,
    int? FastingHours,
    string? CollectionType,
    Guid? SpecimenTypeId,
    LabTestDefinition? SingleTest);

// ---------------------------------------------------------------------------

public sealed class ResolveReceptionOrderHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops)
{
    public async Task<ReceptionOrderView?> HandleAsync(Guid orgId, string serviceRequestId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var order = await hapi.GetLabOrderAsync(serviceRequestId, partition, ct);
        if (order is null)
        {
            return null;
        }
        var resolution = await LabRouting.ResolveOrderAsync(catalog, orgId, order.CatalogCode, ct);
        var existing = await ops.GetIntakeByServiceRequestAsync(orgId, serviceRequestId, ct);

        return new ReceptionOrderView(
            ServiceRequestId: serviceRequestId,
            PatientFhirId: order.PatientFhirId,
            LoincCode: order.LoincCode,
            CodeText: order.CodeText,
            Routing: resolution.Routing.ToString(),
            FastingHours: resolution.FastingHours,
            AlreadyReceived: existing?.Status == OrderIntakeStatus.Received,
            IntakeStatus: existing?.Status.ToString());
    }
}

// ---------------------------------------------------------------------------

public sealed class CreateExternalOrderHandler(IHapiFhirClient hapi)
{
    public async Task<CreatedExternalOrder> HandleAsync(CreateExternalOrderCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.PatientFhirId))
        {
            throw new ArgumentException("PatientFhirId es obligatorio.", nameof(cmd));
        }
        if (string.IsNullOrWhiteSpace(cmd.RequesterName))
        {
            throw new ArgumentException("RequesterName (médico externo) es obligatorio.", nameof(cmd));
        }
        var partition = LabRouting.PartitionOf(cmd.OrganizationId);

        // Médico externo: reusar si ya existe (dedupe), si no crear (LIS-DEC-10).
        var practId = await hapi.FindExternalPractitionerByNameAsync(cmd.RequesterName, partition, ct)
            ?? await hapi.CreateExternalPractitionerAsync(
                new ExternalPractitionerRequest(partition, cmd.RequesterName, cmd.RequesterIdentifier), ct);

        var srId = await hapi.CreateLabOrderAsync(new UpsertLabOrderRequest(
            PartitionName: partition,
            PatientFhirId: cmd.PatientFhirId,
            EncounterFhirId: null,
            RequesterPractitionerId: practId,
            Status: "active",
            Intent: "order",
            LoincCode: cmd.LoincCode,
            CodeDisplay: null,
            CodeText: cmd.CodeText,
            Priority: cmd.Priority,
            OccurrenceDateTime: cmd.OccurrenceDateTime,
            ReasonText: cmd.ReasonText,
            Note: cmd.Note), ct);

        return new CreatedExternalOrder(srId);
    }
}

// ---------------------------------------------------------------------------

public sealed class ReceiveSpecimenHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    IUserPractitionerAssignmentRepository assignments,
    ILocationRepository locations,
    ResolveActiveLocationHandler activeLocation,
    IClinicClock clock,
    TimeProvider timeProvider,
    ILogger<ReceiveSpecimenHandler> logger)
{
    public async Task<ReceiveResult> HandleAsync(ReceiveCommand cmd, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(cmd.OrganizationId);
        var order = await hapi.GetLabOrderAsync(cmd.ServiceRequestId, partition, ct)
            ?? throw new KeyNotFoundException($"La orden '{cmd.ServiceRequestId}' no existe.");

        var existing = await ops.GetIntakeByServiceRequestAsync(cmd.OrganizationId, cmd.ServiceRequestId, ct);
        if (existing is { Status: OrderIntakeStatus.Received })
        {
            throw new InvalidOperationException("La orden ya fue recibida.");
        }

        var resolution = await LabRouting.ResolveOrderAsync(catalog, cmd.OrganizationId, order.CatalogCode, ct);
        var priority = LabRouting.ParsePriority(order.Priority);
        var fastingRequired = resolution.FastingHours is not null;

        // Send-out: no entra a la worklist; se registra el intake como referido.
        if (resolution.Routing == OrderRouting.SendOut)
        {
            var sendOut = OrderIntake.Create(
                cmd.OrganizationId, cmd.ServiceRequestId, order.PatientFhirId, OrderRouting.SendOut,
                priority, fastingRequired: false, cmd.ScheduledFor, cmd.IsWalkIn, timeProvider);
            sendOut.DescribeExam(order.LoincCode, order.CodeText, timeProvider);
            sendOut.MarkCompleted(timeProvider);
            await ops.AddIntakeAsync(sendOut, ct);
            return new ReceiveResult(sendOut.Id, sendOut.Status.ToString(), "SendOut", null, null, null);
        }

        // Precondición: ayuno no cumplido → bloquear (sin crear muestra) (LIS-DEC-14).
        if (fastingRequired && cmd.FastingSatisfied == false)
        {
            var blocked = OrderIntake.Create(
                cmd.OrganizationId, cmd.ServiceRequestId, order.PatientFhirId, OrderRouting.InHouse,
                priority, fastingRequired: true, cmd.ScheduledFor, cmd.IsWalkIn, timeProvider);
            blocked.DescribeExam(order.LoincCode, order.CodeText, timeProvider);
            blocked.MarkBlocked($"Precondición no cumplida: requiere ayuno de {resolution.FastingHours}h.", cmd.UserId, timeProvider);
            await ops.AddIntakeAsync(blocked, ct);
            return new ReceiveResult(blocked.Id, blocked.Status.ToString(), "InHouse", null, null, blocked.BlockReason);
        }

        // Recepción in-house: accession + Specimen + intake + evento + provenance.
        var now = timeProvider.GetUtcNow();
        // El número de accesión lleva la fecha adentro y su contador se reinicia cada día: va con
        // el día de la clínica. `now` sigue siendo UTC para las marcas de tiempo del registro.
        var day = clock.Today();
        var locationId = await activeLocation.HandleAsync(cmd.OrganizationId, cmd.UserId, null, ct);
        var locationCode = (await locations.GetAsync(cmd.OrganizationId, locationId, ct))?.Code;
        var seq = await ops.NextAccessionSeqAsync(cmd.OrganizationId, locationId, day, ct);
        var accession = LabAccession.Build(cmd.OrganizationId, locationCode, day, seq);

        var specimenId = await hapi.CreateSpecimenAsync(new UpsertSpecimenRequest(
            PartitionName: partition,
            PatientFhirId: order.PatientFhirId,
            ServiceRequestFhirId: cmd.ServiceRequestId,
            Status: "available",
            TypeText: cmd.TypeText ?? resolution.CollectionType,
            AccessionNumber: accession,
            ReceivedTime: now.ToString("o"),
            CollectedDateTime: cmd.CollectedDateTime,
            FastingHours: resolution.FastingHours,
            Note: cmd.Note), ct);

        var intake = OrderIntake.Create(
            cmd.OrganizationId, cmd.ServiceRequestId, order.PatientFhirId, OrderRouting.InHouse,
            priority, fastingRequired, cmd.ScheduledFor, cmd.IsWalkIn, timeProvider);
        intake.DescribeExam(order.LoincCode, order.CodeText, timeProvider);
        intake.MarkReceived(specimenId, accession, cmd.UserId, cmd.FastingSatisfied, timeProvider);
        await ops.AddIntakeAsync(intake, ct);
        await ops.AddSpecimenEventAsync(
            SpecimenEvent.Create(intake.Id, SpecimenEventType.Received, cmd.UserId, timeProvider: timeProvider), ct);

        // Provenance (cadena de custodia) best-effort: requiere el Practitioner del técnico.
        var assignment = await assignments.GetAsync(cmd.UserId, cmd.OrganizationId, ct);
        if (assignment is not null && !string.IsNullOrWhiteSpace(assignment.PractitionerFhirId))
        {
            try
            {
                await hapi.CreateProvenanceAsync(new CreateProvenanceRequest(
                    PartitionName: partition,
                    TargetReference: $"Specimen/{specimenId}",
                    ActivityCode: "CREATE",
                    ActivityDisplay: "Recepción de muestra",
                    AgentWhoReference: $"Practitioner/{assignment.PractitionerFhirId}",
                    Recorded: now.ToString("o")), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo registrar Provenance para Specimen {Id} (best-effort).", specimenId);
            }
        }

        return new ReceiveResult(intake.Id, intake.Status.ToString(), "InHouse", accession, specimenId, null);
    }
}
