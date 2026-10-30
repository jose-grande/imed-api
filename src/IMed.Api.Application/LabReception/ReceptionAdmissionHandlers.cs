using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Lab;
using IMed.Api.Domain.Locations;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Rediseño por estaciones — Fase 1 (estación Recepción). Worklist de órdenes
/// **solicitadas por un médico pero aún no admitidas** (sin check-in y sin intake).
/// Es el inverso de <see cref="GetPendingOrdersHandler"/> (que muestra las ya
/// admitidas): acá el recepcionista ve lo que falta dar por admitido. Agrupa por
/// requisición (número de orden) con nombre del paciente y disponibilidad por examen.
///
/// <para><b>Por sede (FAC-9).</b> La orden todavía no tiene sede —la toma al admitirse—, así que
/// se reparte por la sede de la consulta que la pidió; la que no tiene consulta, o es anterior al
/// eje, cuenta como de la principal. <c>allLocations</c> muestra las de todas: el paciente puede
/// presentarse en el laboratorio de otra sucursal, y al admitirlo la orden pasa a esa.</para>
/// </summary>
public sealed class GetAwaitingAdmissionHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops,
    IServiceGate gate, ResolveActiveLocationHandler activeLocation)
{
    public async Task<IReadOnlyList<AwaitingAdmissionOrder>> HandleAsync(
        Guid orgId, Guid? userId, bool allLocations = false, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var sede = allLocations ? null : await activeLocation.ResolveScopeAsync(orgId, userId, ct);
        var orders = await hapi.SearchActiveLabOrdersAsync(partition, ct);
        var checkedIn = await ops.GetCheckedInRequisitionsAsync(orgId, ct);

        var groups = new Dictionary<string, AwaitingGroupBuilder>();
        var patientNames = new Dictionary<string, string?>();
        var encounterSedes = new Dictionary<string, Guid?>();

        foreach (var o in orders)
        {
            // Ya admitida (check-in) → no está "por admitir".
            if (o.Requisition is not null && checkedIn.Contains(o.Requisition))
            {
                continue;
            }
            // Ya tiene intake (referida/recibida/bloqueada/rechazada) → fuera de "por admitir".
            var intake = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
            if (intake is not null)
            {
                continue;
            }

            if (sede is not null)
            {
                Guid? origen = null;
                if (o.EncounterFhirId is { } enc && !encounterSedes.TryGetValue(enc, out origen))
                {
                    origen = OrganizationLocation.FromFhirReference(
                        await hapi.GetEncounterLocationAsync(enc, partition, ct));
                    encounterSedes[enc] = origen;
                }
                if (!sede.Includes(origen))
                {
                    continue;
                }
            }

            var key = o.Requisition ?? $"__sr__:{o.Id}";
            if (!groups.TryGetValue(key, out var g))
            {
                if (!patientNames.TryGetValue(o.PatientFhirId, out var name))
                {
                    var p = await hapi.GetPatientAsync(o.PatientFhirId, partition, ct);
                    name = p?.DisplayName;
                    patientNames[o.PatientFhirId] = name;
                }
                g = new AwaitingGroupBuilder(o.Requisition, o.PatientFhirId, name, o.EncounterFhirId);
                groups[key] = g;
            }
            var availability = await ClassifyAsync(catalog, orgId, o.CatalogCode, ct);

            // FAC-7: el LIS **consulta** el estado de cobro, no lo calcula. Y lo muestra sin
            // bloquear: un sistema que impide recibir la muestra se evade —el técnico anota en
            // papel— y eso es peor que no tener el control.
            var payment = await gate.EvaluateAsync(orgId, o.Id, o.Priority, ct);

            g.Exams.Add(new AwaitingAdmissionExam(
                o.Id, o.LoincCode, o.CodeText, availability,
                payment.Verdict, payment.Message));
        }

        return groups.Values
            .Select(g => new AwaitingAdmissionOrder(g.Requisition, g.PatientFhirId, g.PatientName, g.EncounterFhirId, g.Exams))
            .ToList();
    }

    /// <summary>Disponibilidad agregada de un examen/panel: in-house / send-out / partial / unknown.</summary>
    private static async Task<string> ClassifyAsync(ILabCatalogRepository catalog, Guid orgId, string? code, CancellationToken ct)
    {
        var expanded = await LabRouting.ExpandOrderAsync(catalog, orgId, code, ct);
        if (expanded.Count == 0)
        {
            return "unknown";
        }
        var inHouse = expanded.Count(e => e.Routing == OrderRouting.InHouse);
        if (inHouse == expanded.Count)
        {
            return "in-house";
        }
        return inHouse == 0 ? "send-out" : "partial";
    }

    private sealed class AwaitingGroupBuilder(string? requisition, string patientFhirId, string? patientName, string? encounterFhirId)
    {
        public string? Requisition { get; } = requisition;
        public string PatientFhirId { get; } = patientFhirId;
        public string? PatientName { get; } = patientName;
        public string? EncounterFhirId { get; } = encounterFhirId;
        public List<AwaitingAdmissionExam> Exams { get; } = [];
    }
}

/// <summary>
/// Rediseño por estaciones — Fase 1 (estación Recepción). Crea una orden externa con
/// **varios exámenes/paneles** (a diferencia de <see cref="CreateExternalOrderHandler"/>,
/// que es de un solo examen): dedupe/crea el médico externo, asigna un número de orden
/// (requisición) y crea N <c>ServiceRequest</c> que la comparten (dual coding). La
/// admisión (check-in) la hace el frontend con la requisición devuelta, reusando el
/// split in-house/send-out del flujo de check-in.
/// </summary>
public sealed class CreateExternalOrderMultiHandler(
    IHapiFhirClient hapi, ILabOperationsRepository ops, ILabCatalogRepository catalog,
    ResolveActiveLocationHandler activeLocation, IClinicClock clock)
{
    public async Task<CreatedExternalOrderMulti> HandleAsync(CreateExternalOrderMultiCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.PatientFhirId))
        {
            throw new ArgumentException("PatientFhirId es obligatorio.", nameof(cmd));
        }
        if (string.IsNullOrWhiteSpace(cmd.RequesterName))
        {
            throw new ArgumentException("El médico solicitante externo es obligatorio.", nameof(cmd));
        }
        if (cmd.Exams.Count == 0)
        {
            throw new ArgumentException("Agregá al menos un examen o panel.", nameof(cmd));
        }

        // Antes de crear nada en HAPI: una orden a medio crear deja exámenes sueltos.
        await ValidateAgainstCatalogAsync(cmd.OrganizationId, cmd.Exams, ct);

        var partition = LabRouting.PartitionOf(cmd.OrganizationId);

        // Médico externo: reusar si ya existe (dedupe), si no crear (LIS-DEC-10).
        var practId = await hapi.FindExternalPractitionerByNameAsync(cmd.RequesterName, partition, ct)
            ?? await hapi.CreateExternalPractitionerAsync(
                new ExternalPractitionerRequest(partition, cmd.RequesterName, cmd.RequesterIdentifier), ct);

        // El correlativo lleva la fecha adentro y se reinicia cada día: tiene que ser el día de la
        // clínica. Con UTC, una admisión de la tarde salía con la fecha de mañana y el contador
        // arrancado de nuevo.
        var day = clock.Today();
        // Con el usuario: la sede pedida en el request se verifica contra sus asignaciones, y sin
        // pedido vale su principal, no la de la organización.
        var locationId = await activeLocation.HandleAsync(cmd.OrganizationId, cmd.UserId, null, ct);
        var seq = await ops.NextOrderSeqAsync(cmd.OrganizationId, locationId, day, ct);
        var requisition = $"ORD-{day:yyyyMMdd}-{seq:D4}";

        var note = ComposeNote(cmd.RequesterInstitution, cmd.Note);
        var ids = new List<string>();
        foreach (var exam in cmd.Exams)
        {
            var id = await hapi.CreateLabOrderAsync(new UpsertLabOrderRequest(
                PartitionName: partition,
                PatientFhirId: cmd.PatientFhirId,
                EncounterFhirId: null,
                RequesterPractitionerId: practId,
                Status: "active",
                Intent: "order",
                LoincCode: exam.LoincCode,
                CodeDisplay: exam.CodeDisplay,
                CodeText: exam.CodeText,
                Priority: cmd.Priority,
                OccurrenceDateTime: cmd.OccurrenceDateTime,
                ReasonText: cmd.ReasonText,
                Note: note,
                Requisition: requisition,
                LocalCode: exam.LocalCode,
                LocalSystem: exam.LocalSystem), ct);
            ids.Add(id);
        }

        return new CreatedExternalOrderMulti(requisition, ids);
    }

    /// <summary>
    /// En recepción solo se cargan pruebas y paneles parametrizados que el laboratorio hace; lo
    /// demás se refiere afuera y no entra al sistema. (La orden del médico no tiene esta
    /// restricción: puede pedir cualquier prueba.)
    ///
    /// <para>Además de que exista, se verifica que el código que va a quedar en la orden lleve a
    /// <b>esa</b> prueba o panel: recepción, toma, etiquetas y resultados la encuentran por ese
    /// código (<see cref="LabRouting.ExpandOrderAsync"/>), y de ahí sacan tipo de muestra, tubo,
    /// ayuno y rangos. Si dos pruebas comparten LOINC, o una prueba comparte código con un panel,
    /// la orden terminaría resolviendo a otra, con la parametrización de otra.</para>
    /// </summary>
    private async Task ValidateAgainstCatalogAsync(
        Guid orgId, IReadOnlyList<ExternalOrderExam> exams, CancellationToken ct)
    {
        var noOfertadas = await catalog.ListNotOfferedTestIdsAsync(orgId, ct);
        foreach (var exam in exams)
        {
            var nombre = exam.CodeDisplay ?? exam.CodeText;
            // El mismo código con que la leen las demás estaciones (LabOrderSnapshot.CatalogCode).
            var code = !string.IsNullOrWhiteSpace(exam.LoincCode) ? exam.LoincCode.Trim() : exam.LocalCode?.Trim();
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException(
                    $"«{nombre}» no está en el catálogo del laboratorio. Solo se cargan pruebas y paneles parametrizados; lo demás se refiere afuera.");
            }

            var esPanel = string.Equals(exam.LocalSystem, LabOrderCodeSystems.Panel, StringComparison.Ordinal);
            var panel = await catalog.GetPanelByCodeOrLoincAsync(orgId, code, ct);
            if (panel is not null)
            {
                if (!esPanel)
                {
                    throw new ArgumentException(
                        $"El código {code} de «{nombre}» es también el de un panel del catálogo. Corregí el código de la prueba o del panel en el catálogo.");
                }
                var pruebas = await LabRouting.ExpandOrderAsync(catalog, orgId, code, ct);
                if (!pruebas.Any(p => p.Routing == OrderRouting.InHouse))
                {
                    throw new ArgumentException(
                        $"Ninguna prueba del panel «{panel.Name}» se hace en el laboratorio: hay que referirlo afuera.");
                }
                continue;
            }
            if (esPanel)
            {
                throw new ArgumentException($"El panel «{nombre}» no está activo en el catálogo del laboratorio.");
            }

            var test = await catalog.GetTestByCodeOrLoincAsync(orgId, code, ct);
            if (test is null || !test.Active)
            {
                throw new ArgumentException(
                    $"«{nombre}» no está en el catálogo del laboratorio. Solo se cargan pruebas y paneles parametrizados; lo demás se refiere afuera.");
            }
            if (!string.IsNullOrWhiteSpace(exam.LocalCode) && !string.Equals(test.Code, exam.LocalCode.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"El LOINC {code} lo tienen varias pruebas del catálogo ({test.Code} y {exam.LocalCode.Trim()}). Dejalo en una sola para que la orden tome los rangos y la muestra correctos.");
            }
            if (noOfertadas.Contains(test.Id))
            {
                throw new ArgumentException($"«{test.Name}» no se hace en el laboratorio: hay que referirla afuera.");
            }
        }
    }

    /// <summary>La institución externa (trazabilidad) se anota en la nota de la orden (MVP Fase 1).</summary>
    private static string? ComposeNote(string? institution, string? note)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(institution))
        {
            parts.Add($"Institución: {institution.Trim()}");
        }
        if (!string.IsNullOrWhiteSpace(note))
        {
            parts.Add(note.Trim());
        }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }
}

// ── DTOs (Fase 1) ───────────────────────────────────────────────────────────

/// <summary>
/// Un examen dentro de una orden esperando admisión, con su disponibilidad y su estado de cobro.
/// </summary>
/// <param name="PaymentStatus">
/// <c>Authorized</c> / <c>PendingPayment</c> / <c>Waived</c>. <b>Es información, no una orden</b>:
/// la recepción lo muestra y deja continuar.
/// </param>
/// <param name="PaymentMessage">Texto listo para mostrar al técnico.</param>
public sealed record AwaitingAdmissionExam(
    string ServiceRequestId,
    string? LoincCode,
    string? CodeText,
    string Availability,
    string? PaymentStatus = null,
    string? PaymentMessage = null);

/// <summary>Una orden solicitada aún no admitida, agrupada por requisición.</summary>
public sealed record AwaitingAdmissionOrder(
    string? Requisition, string PatientFhirId, string? PatientName, string? EncounterFhirId,
    IReadOnlyList<AwaitingAdmissionExam> Exams);

/// <summary>Un examen/panel a crear en una orden externa (dual coding opcional).</summary>
/// <summary>
/// Sistemas de los códigos propios de la clínica en una orden. Van junto al LOINC (dual coding):
/// el LOINC es el significado compartido con el mundo; el código local es la prueba o el panel
/// exactos del catálogo, y es el único que tienen las pruebas sin LOINC.
/// </summary>
public static class LabOrderCodeSystems
{
    public const string Panel = "http://imed.local/panel";
    public const string Test = "http://imed.local/lab-test";
}

public sealed record ExternalOrderExam(string? LoincCode, string? LocalCode, string? LocalSystem, string CodeText, string? CodeDisplay);

public sealed record CreateExternalOrderMultiCommand(
    Guid OrganizationId, string PatientFhirId, string RequesterName, string? RequesterIdentifier,
    string? RequesterInstitution, string? Priority, string? OccurrenceDateTime, string? ReasonText, string? Note,
    IReadOnlyList<ExternalOrderExam> Exams,
    Guid? UserId = null);

public sealed record CreatedExternalOrderMulti(string Requisition, IReadOnlyList<string> ServiceRequestIds);
