using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// R6-F4: genera accession + barcode por contenedor del plan (etiqueta temprana,
/// antes de la toma — decisión UX §8.4). Idempotente: solo etiqueta los contenedores
/// que aún no tienen accession, así reusar el endpoint no consume números de más.
/// </summary>
public sealed class GenerateLabelsHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<CollectionPlanDto?> HandleAsync(Guid orgId, string requisition, Guid? userId, CancellationToken ct = default)
    {
        var plan = await ops.GetCollectionPlanForUpdateAsync(orgId, requisition, ct);
        if (plan is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var events = new List<CollectionEvent>();
        foreach (var c in plan.Containers)
        {
            if (c.AccessionNumber is not null)
            {
                continue;
            }
            var seq = await ops.NextAccessionSeqAsync(orgId, day, ct);
            var accession = $"{orgId:N}"[..4].ToUpperInvariant() + $"-{day:yyyyMMdd}-{seq:D4}";
            c.AssignLabel(accession, accession); // barcode = accession (Code128, igual que R1-F3)
            events.Add(CollectionEvent.Create(c.Id, CollectionEventType.LabelGenerated, userId, accession, timeProvider));
        }
        if (events.Count > 0)
        {
            await ops.SaveCollectionEventsAsync(events, ct);
        }
        return CollectionPlanDto.FromDomain(plan);
    }
}

/// <summary>
/// R6-F3: avanza el ciclo de vida de un contenedor (imprimir/iniciar toma/tomar/recibir/
/// rechazar/almacenar) y registra el evento de timeline (cadena de custodia). La
/// reimpresión y el rechazo exigen motivo (decisión §8 / D9).
/// </summary>
public sealed class AdvanceContainerHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<CollectionPlanContainerDto?> HandleAsync(
        Guid orgId, Guid containerId, string action, string? detail, Guid? userId, CancellationToken ct = default)
    {
        var c = await ops.GetContainerForUpdateAsync(orgId, containerId, ct);
        if (c is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        CollectionEventType eventType;
        switch (action.ToLowerInvariant())
        {
            case "print":
                if (c.Status == ContainerStatus.LabelGenerated)
                {
                    c.MarkPrinted();
                    eventType = CollectionEventType.LabelPrinted;
                }
                else
                {
                    // Reimpresión de una muestra que ya avanzó → motivo obligatorio (D9).
                    if (string.IsNullOrWhiteSpace(detail))
                    {
                        throw new ArgumentException("La reimpresión requiere un motivo.", nameof(detail));
                    }
                    eventType = CollectionEventType.LabelReprinted;
                }
                break;
            case "start":
                c.StartCollection();
                eventType = CollectionEventType.CollectionStarted;
                break;
            case "collect":
                // R6-F6: la toma exige identificación positiva previa del paciente
                // (decisión §8.6). El workbench la registra justo antes vía verify-identity;
                // el gate también blinda llamadas directas al gateway.
                var priorEvents = await ops.ListCollectionEventsAsync(c.Id, ct);
                if (!priorEvents.Any(e => e.Type == CollectionEventType.IdentityVerified))
                {
                    throw new ArgumentException(
                        "La toma requiere identificación positiva del paciente (nombre completo + fecha de nacimiento).",
                        nameof(action));
                }
                c.MarkCollected(now);
                eventType = CollectionEventType.Collected;
                break;
            case "receive":
                c.MarkReceived(now);
                eventType = CollectionEventType.Received;
                break;
            case "reject":
                if (string.IsNullOrWhiteSpace(detail))
                {
                    throw new ArgumentException("El rechazo requiere un motivo.", nameof(detail));
                }
                c.Reject(detail!);
                eventType = CollectionEventType.Rejected;
                break;
            case "store":
                c.Store(detail);
                eventType = CollectionEventType.Stored;
                break;
            default:
                throw new ArgumentException($"Acción desconocida: {action}.", nameof(action));
        }

        await ops.SaveCollectionEventsAsync(
            [CollectionEvent.Create(c.Id, eventType, userId, detail, timeProvider)], ct);
        return CollectionPlanContainerDto.FromDomain(c);
    }
}

/// <summary>R6-F3: timeline de cadena de custodia de un contenedor.</summary>
public sealed class GetContainerTimelineHandler(ILabOperationsRepository ops)
{
    public async Task<IReadOnlyList<CollectionEventDto>> HandleAsync(Guid containerId, CancellationToken ct = default)
    {
        var events = await ops.ListCollectionEventsAsync(containerId, ct);
        return events.Select(CollectionEventDto.FromDomain).ToList();
    }
}

/// <summary>
/// R6-F6: identificación positiva del paciente (decisión §8.6). Registra como evidencia
/// de la cadena de custodia que el operador verificó **nombre completo + fecha de
/// nacimiento** antes de la toma (o en admisión). Ambos identificadores son obligatorios;
/// la toma (<c>collect</c>) queda bloqueada hasta que exista este evento.
/// </summary>
public sealed class VerifyIdentityHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<CollectionPlanContainerDto?> HandleAsync(
        Guid orgId, Guid containerId, bool verifiedName, bool verifiedBirthDate,
        string stage, string? patientName, string? patientBirthDate, Guid? userId, CancellationToken ct = default)
    {
        var c = await ops.GetContainerForUpdateAsync(orgId, containerId, ct);
        if (c is null)
        {
            return null;
        }

        if (!verifiedName || !verifiedBirthDate)
        {
            throw new ArgumentException(
                "La identificación positiva exige verificar nombre completo y fecha de nacimiento.");
        }

        var stageLabel = string.Equals(stage, "Admission", StringComparison.OrdinalIgnoreCase) ? "Admisión" : "Toma";
        var detailParts = new List<string> { stageLabel };
        if (!string.IsNullOrWhiteSpace(patientName))
        {
            detailParts.Add(patientName.Trim());
        }
        if (!string.IsNullOrWhiteSpace(patientBirthDate))
        {
            detailParts.Add(patientBirthDate.Trim());
        }

        await ops.SaveCollectionEventsAsync(
            [CollectionEvent.Create(c.Id, CollectionEventType.IdentityVerified, userId, string.Join(" · ", detailParts), timeProvider)], ct);
        return CollectionPlanContainerDto.FromDomain(c);
    }
}

/// <summary>
/// R6-F6: control de calidad preanalítico de un contenedor (decisión §8.7). Acepta la
/// muestra (→ recibida técnicamente) o la rechaza con un **motivo del catálogo codificado**
/// (<see cref="IMed.Api.Domain.Catalogs.Lab.SpecimenRejectionReason"/>). El rechazo indica
/// si operativamente exige solicitar una nueva muestra.
/// </summary>
public sealed class QualityControlHandler(
    ILabOperationsRepository ops,
    ILabCatalogRepository catalog,
    CollectionIntakeConvergence convergence,
    TimeProvider timeProvider)
{
    public async Task<QualityControlResultDto?> HandleAsync(
        Guid orgId, Guid containerId, bool accepted, string? rejectionReasonCode, string? note,
        Guid? userId, CancellationToken ct = default)
    {
        var c = await ops.GetContainerForUpdateAsync(orgId, containerId, ct);
        if (c is null)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();

        if (accepted)
        {
            await EnsureIdentityVerifiedAsync(orgId, c, ct);
            c.MarkReceived(now);
            var detail = string.IsNullOrWhiteSpace(note) ? "QC conforme" : $"QC conforme · {note.Trim()}";
            await ops.SaveCollectionEventsAsync(
                [CollectionEvent.Create(c.Id, CollectionEventType.Received, userId, detail, timeProvider)], ct);

            // La muestra entra al laboratorio: se materializa en el flujo operativo.
            await convergence.ConvergeAsync(orgId, c, userId, accepted: true, rejectionReason: null, ct);

            return new QualityControlResultDto(CollectionPlanContainerDto.FromDomain(c), false, false, null);
        }

        // Rechazo: el motivo debe pertenecer al catálogo (decisión §8.7), no texto libre.
        if (string.IsNullOrWhiteSpace(rejectionReasonCode))
        {
            throw new ArgumentException("El rechazo de QC requiere un motivo del catálogo.", nameof(rejectionReasonCode));
        }

        var reasons = await catalog.ListSpecimenRejectionReasonsAsync(orgId, ct);
        var reason = reasons.FirstOrDefault(r => string.Equals(r.Code, rejectionReasonCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Motivo de rechazo desconocido: {rejectionReasonCode}.", nameof(rejectionReasonCode));

        c.Reject(reason.Code);
        var rejectDetail = reason.Name;
        if (!string.IsNullOrWhiteSpace(note))
        {
            rejectDetail += $" · {note.Trim()}";
        }
        if (reason.RequiresNewSample)
        {
            rejectDetail += " · requiere nueva muestra";
        }
        await ops.SaveCollectionEventsAsync(
            [CollectionEvent.Create(c.Id, CollectionEventType.Rejected, userId, rejectDetail, timeProvider)], ct);

        // El rechazo también se refleja en el flujo operativo: una muestra rechazada tiene que
        // ser visible en la worklist y el dashboard, no desaparecer del radar.
        await convergence.ConvergeAsync(orgId, c, userId, accepted: false, rejectionReason: reason.Name, ct);

        return new QualityControlResultDto(CollectionPlanContainerDto.FromDomain(c), true, reason.RequiresNewSample, reason.Name);
    }


    /// <summary>
    /// Gate de identificación positiva para las muestras que **trae el paciente** (orina, heces).
    ///
    /// <para>Esas muestras no pasan por la estación de toma —llegan ya rotuladas— así que el gate
    /// de <c>collect</c> nunca las alcanza y, sin esto, entrarían a análisis sin que nadie haya
    /// cotejado jamás que el frasco corresponde a esa persona. La recepción técnica es el último
    /// punto donde ese control es posible. Las tomadas on-site ya se verificaron en la toma y no
    /// se les vuelve a pedir.</para>
    ///
    /// <para>Como el gate vive acá y no solo en la UI, también blinda llamadas directas al gateway.</para>
    /// </summary>
    private async Task EnsureIdentityVerifiedAsync(Guid orgId, CollectionPlanContainer c, CancellationToken ct)
    {
        var specimenTypes = await catalog.ListSpecimenTypesAsync(orgId, ct);
        var mode = specimenTypes.FirstOrDefault(s => s.Code == c.SpecimenTypeCode)?.CollectionMode;
        if (mode != CollectionMode.PatientCollected)
        {
            return;
        }

        var events = await ops.ListCollectionEventsAsync(c.Id, ct);
        if (!events.Any(e => e.Type == CollectionEventType.IdentityVerified))
        {
            throw new ArgumentException(
                "La muestra la trajo el paciente y no tiene identificación positiva: verificá nombre completo y fecha de nacimiento antes de recibirla.");
        }
    }
}
