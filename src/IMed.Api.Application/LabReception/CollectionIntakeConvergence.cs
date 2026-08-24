using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Lab;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Puente entre el **ciclo de muestra** del rediseño por estaciones (R6) y el **flujo operativo**
/// (`order_intake`) — la convergencia que quedaba diferida desde R6-F3.
///
/// <para><b>El problema que resuelve:</b> las estaciones nuevas (Recepción → Cola de toma →
/// Recepción técnica) mueven el estado del <see cref="CollectionPlanContainer"/>, pero la worklist
/// de Resultados, el dashboard y el TAT leen <c>order_intake</c>, que solo escribía el flujo de
/// recepción legacy. Sin este puente una muestra recorría todas las estaciones y **nunca aparecía
/// para resultar**; peor, <c>EnterResultsHandler</c> rechazaba la captura con "la orden no fue
/// recibida".</para>
///
/// <para><b>Granularidad:</b> el contenedor es la unidad física y cubre <b>N órdenes</b> (el plan
/// consolida pruebas compatibles en un tubo). Se materializa <b>una</b> muestra FHIR por contenedor,
/// compartida por todas sus órdenes (<c>Specimen.request[]</c>, N:M igual que en la recepción por
/// requisición), y se abre un <c>order_intake</c> por orden — que es la grana del TAT.</para>
///
/// <para><b>Idempotente:</b> si la orden ya tiene intake no se toca. Así reprocesar un QC, o
/// recibir un segundo contenedor de la misma orden, no duplica filas ni reinicia el reloj de TAT.</para>
/// </summary>
public sealed class CollectionIntakeConvergence(
    ILabOperationsRepository ops,
    ILabCatalogRepository catalog,
    IHapiFhirClient hapi,
    IUserPractitionerAssignmentRepository assignments,
    TimeProvider timeProvider,
    ILogger<CollectionIntakeConvergence> logger)
{
    /// <summary>Cota de lectura del catálogo para resolver el ayuno por código de prueba.</summary>
    private const int CatalogLookupLimit = 1000;

    /// <summary>
    /// Registra en el flujo operativo el contenedor recién resuelto por el QC. Devuelve cuántas
    /// órdenes se registraron (0 = ya estaban, o el contenedor no cubre órdenes conocidas).
    /// </summary>
    public async Task<int> ConvergeAsync(
        Guid orgId,
        CollectionPlanContainer container,
        Guid? userId,
        bool accepted,
        string? rejectionReason,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(container);

        var serviceRequestIds = container.Items
            .Select(i => i.ServiceRequestFhirId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (serviceRequestIds.Count == 0)
        {
            return 0;
        }

        var partition = LabRouting.PartitionOf(orgId);
        var now = timeProvider.GetUtcNow();

        // Órdenes que todavía no tienen intake; las ya registradas se dejan como están.
        var toRegister = new List<LabOrderSnapshot>();
        foreach (var srId in serviceRequestIds)
        {
            if (await ops.GetIntakeByServiceRequestAsync(orgId, srId, ct) is not null)
            {
                continue;
            }
            var snapshot = await hapi.GetLabOrderAsync(srId, partition, ct);
            if (snapshot is not null)
            {
                toRegister.Add(snapshot);
            }
        }
        if (toRegister.Count == 0)
        {
            return 0;
        }

        // Ayuno declarado en el catálogo, resuelto por código de prueba en una sola lectura.
        var testsByCode = (await catalog.SearchTestsAsync(orgId, null, null, activeOnly: false, CatalogLookupLimit, ct))
            .GroupBy(t => t.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var fastingHours = container.Items
            .Select(i => testsByCode.GetValueOrDefault(i.LabTestCode)?.FastingHours)
            .Where(h => h is not null)
            .Select(h => h!.Value)
            .DefaultIfEmpty(0)
            .Max();

        // Practitioner del técnico que resolvió el QC → collector de la muestra y agente de la
        // cadena de custodia, igual que en el camino legacy. Sin esto la muestra queda sin firma
        // de quién la recibió y la evidencia solo vive en la tabla local `collection_event`.
        var collectorRef = userId is null
            ? null
            : (await assignments.GetAsync(userId.Value, orgId, ct))?.PractitionerFhirId;
        collectorRef = string.IsNullOrWhiteSpace(collectorRef) ? null : collectorRef;

        // Una muestra FHIR por contenedor, compartida por sus órdenes. Solo si el QC aceptó:
        // una muestra rechazada no ingresa al laboratorio.
        var specimenId = container.SpecimenFhirId;
        if (accepted && specimenId is null && container.AccessionNumber is not null)
        {
            specimenId = await hapi.CreateSpecimenAsync(new UpsertSpecimenRequest(
                PartitionName: partition,
                PatientFhirId: toRegister[0].PatientFhirId,
                ServiceRequestFhirId: serviceRequestIds[0],
                Status: "available",
                TypeText: container.SpecimenTypeName ?? container.SpecimenTypeCode,
                AccessionNumber: container.AccessionNumber,
                ReceivedTime: now.ToString("o"),
                CollectedDateTime: container.CollectedAt?.ToString("o"),
                FastingHours: fastingHours == 0 ? null : fastingHours,
                Note: null,
                AdditionalServiceRequestFhirIds: serviceRequestIds.Skip(1).ToList(),
                CollectorPractitionerId: collectorRef,
                CollectionMethod: null,
                CollectionBodySite: null), ct);
            container.SetSpecimenFhirId(specimenId);

            await TryProvenanceAsync(partition, collectorRef, specimenId, now, ct);
        }

        foreach (var order in toRegister)
        {
            var intake = OrderIntake.Create(
                orgId,
                order.Id,
                order.PatientFhirId,
                OrderRouting.InHouse, // se tomó y procesó en la sede
                LabRouting.ParsePriority(order.Priority),
                fastingRequired: fastingHours > 0,
                timeProvider: timeProvider);

            intake.Classify(order.Requisition, container.SpecimenTypeCode, container.TubeTypeCode, timeProvider);
            intake.DescribeExam(order.LoincCode, order.CodeText, timeProvider);

            if (accepted && specimenId is not null && container.AccessionNumber is not null)
            {
                // Arranca el reloj de TAT: la muestra ya está en el laboratorio.
                intake.MarkReceived(specimenId, container.AccessionNumber, userId ?? Guid.Empty, null, timeProvider);
            }
            else if (!accepted)
            {
                intake.MarkRejected(
                    rejectionReason ?? "Rechazada en control de calidad", userId ?? Guid.Empty, timeProvider);
            }

            await ops.AddIntakeAsync(intake, ct);
            await ops.AddSpecimenEventAsync(
                SpecimenEvent.Create(
                    intake.Id,
                    accepted ? SpecimenEventType.Received : SpecimenEventType.Rejected,
                    userId,
                    timeProvider: timeProvider),
                ct);
        }

        return toRegister.Count;
    }

    /// <summary>
    /// Cadena de custodia de la muestra en FHIR. Best-effort y con el mismo contrato que el camino
    /// legacy (<c>ReceiveRequisitionHandler</c>): si no hay Practitioner detrás del usuario no hay
    /// a quién atribuirle el acto, y un fallo de HAPI no puede tumbar una recepción ya registrada.
    /// </summary>
    private async Task TryProvenanceAsync(
        string partition, string? collectorRef, string specimenId, DateTimeOffset now, CancellationToken ct)
    {
        if (collectorRef is null)
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
