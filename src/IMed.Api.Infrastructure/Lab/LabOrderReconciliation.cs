using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Lab;

/// <summary>
/// Promueve a <c>active</c> las órdenes de laboratorio que quedaron en <c>draft</c> sobre consultas
/// ya cerradas.
///
/// <para><b>Por qué existe.</b> El cierre de consulta es atómico —un Bundle de transacción—, pero
/// la promoción de las órdenes <b>no forma parte de él</b>: la hace el navegador después, con un
/// PUT por orden. Entre que la transacción confirma y ese PUT llega hay una ventana donde cualquier
/// cosa pierde la orden: un 500, el wifi de la clínica, el token que expira, la pestaña que se
/// cierra o la laptop que se suspende justo después de leer "Consulta cerrada". Con varias órdenes
/// puede además promover unas sí y otras no.</para>
///
/// <para>El daño de cada una de esas es idéntico y grave: el médico cree que pidió el estudio, el
/// paciente se va, el laboratorio nunca lo ve, y nadie se entera hasta que alguien pregunta por un
/// resultado que no existe. Y no se repara solo, porque nada más mira esas órdenes.</para>
///
/// <para><b>Por qué un barrido y no un aviso en pantalla.</b> Un aviso solo sirve si la pestaña
/// sigue abierta y hay alguien mirando — justo lo que no pasa en el caso que más preocupa. El
/// barrido es indiferente a la causa: no necesita saber por qué falló, ni siquiera que existió una
/// falla. Repara también lo que se perdió antes de que este código existiera.</para>
///
/// <para><b>Es idempotente</b>: promover algo que ya está en <c>active</c> no lo encuentra, porque
/// la búsqueda pide <c>draft</c>. Correrlo de más no cuesta nada.</para>
/// </summary>
public sealed class LabOrderReconciliation(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LabOrderReconciliationOptions> optionsMonitor,
    TimeProvider timeProvider,
    ILogger<LabOrderReconciliation> logger) : BackgroundService
{
    /// <summary>Categoría en <c>app_audit_log</c>. Una orden que se repara sola tiene que dejar rastro.</summary>
    public const string AuditCategory = "lab_order_reconciliation";

    /// <summary>
    /// Estados de <c>Encounter</c> que cuentan como consulta terminada. <c>completed</c> es R5;
    /// <c>finished</c> es el nombre que tenía en R4 y sigue apareciendo en datos migrados.
    /// </summary>
    private static readonly string[] ClosedEncounterStates = ["completed", "finished"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("LabOrderReconciliation iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            try
            {
                if (options.Enabled)
                {
                    await RunOnceAsync(options, stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Una vuelta que falla no puede tumbar el worker: la próxima vuelta reintenta, y
                // las órdenes siguen ahí esperando. Es una red de seguridad, no un camino crítico.
                logger.LogError(ex, "LabOrderReconciliation: error en el barrido. Se reintenta en la próxima vuelta.");
            }

            await DelayAsync(TimeSpan.FromSeconds(Math.Max(30, options.PollSeconds)), stoppingToken);
        }

        logger.LogInformation("LabOrderReconciliation detenido.");
    }

    /// <summary>Un barrido completo. Público para que las pruebas no dependan del temporizador.</summary>
    public async Task<int> RunOnceAsync(LabOrderReconciliationOptions options, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hapi = scope.ServiceProvider.GetRequiredService<IHapiFhirClient>();

        // HAPI está particionado por organización y no hay búsqueda cruzada, así que se recorre
        // organización por organización. La lista sale de las suscripciones: toda organización
        // aprovisionada tiene una, y una que no la tenga tampoco tiene usuarios que pidan estudios.
        var organizations = await db.OrganizationSubscriptions.AsNoTracking()
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(ct);

        var cutoff = timeProvider.GetUtcNow().AddMinutes(-Math.Max(0, options.GraceMinutes));
        var promoted = 0;

        foreach (var orgId in organizations)
        {
            if (promoted >= options.MaxPerRun || ct.IsCancellationRequested)
            {
                break;
            }

            promoted += await ReconcileOrganizationAsync(
                db, hapi, orgId, cutoff, options.MaxPerRun - promoted, ct);
        }

        if (promoted > 0)
        {
            logger.LogWarning(
                "LabOrderReconciliation: {Count} orden(es) de laboratorio quedaron sin emitir al cerrar la consulta y se promovieron.",
                promoted);
        }

        return promoted;
    }

    private async Task<int> ReconcileOrganizationAsync(
        AppDbContext db,
        IHapiFhirClient hapi,
        Guid organizationId,
        DateTimeOffset cutoff,
        int budget,
        CancellationToken ct)
    {
        var partition = $"org-{organizationId}";
        var drafts = await hapi.SearchDraftLabOrdersAsync(partition, ct);

        var candidates = drafts
            .Where(o => !string.IsNullOrWhiteSpace(o.EncounterFhirId))
            // Sin fecha se la considera vieja. Descartarla sería más prudente en apariencia y peor
            // en la práctica: una orden sin `authoredOn` sobre un encuentro cerrado no se repararía
            // nunca, que es exactamente el desenlace que este barrido existe para evitar. El único
            // costo de equivocarse es chocar con una promoción simultánea del cliente, y de eso se
            // sale reintentando en la vuelta siguiente.
            .Where(o => o.AuthoredOn is not { } authored || authored <= cutoff)
            .Take(budget)
            .ToList();

        if (candidates.Count == 0)
        {
            return 0;
        }

        // El estado del encuentro se consulta una vez por encuentro y no una vez por orden: una
        // consulta con seis estudios son seis órdenes y un solo encuentro.
        var encounterStates = new Dictionary<string, string?>(StringComparer.Ordinal);
        var promoted = 0;

        foreach (var order in candidates)
        {
            var encounterId = order.EncounterFhirId!;
            if (!encounterStates.TryGetValue(encounterId, out var state))
            {
                state = await hapi.GetEncounterStatusAsync(encounterId, partition, ct);
                encounterStates[encounterId] = state;
            }

            if (state is null || !ClosedEncounterStates.Contains(state, StringComparer.OrdinalIgnoreCase))
            {
                // La consulta sigue abierta: el borrador es exactamente lo que tiene que ser.
                continue;
            }

            await PromoteAsync(db, hapi, partition, organizationId, order, ct);
            promoted++;
        }

        return promoted;
    }

    private async Task PromoteAsync(
        AppDbContext db,
        IHapiFhirClient hapi,
        string partition,
        Guid organizationId,
        LabOrderSnapshot order,
        CancellationToken ct)
    {
        // Se reescribe la orden entera tal como estaba, cambiando solo el estado. Los códigos van
        // cada uno con su sistema: republicar el código del panel bajo LOINC sería reintroducir por
        // acá el mismo dato falso que se acaba de corregir en la lectura.
        var request = new UpsertLabOrderRequest(
            PartitionName: partition,
            PatientFhirId: order.PatientFhirId,
            EncounterFhirId: order.EncounterFhirId,
            RequesterPractitionerId: order.RequesterPractitionerId,
            Status: "active",
            Intent: order.Intent,
            LoincCode: order.LoincCode,
            CodeDisplay: order.CodeDisplay,
            // El texto es obligatorio aguas abajo. Si la orden no lo trae, se cae al display o al
            // código antes que abandonarla en `draft`: una orden con la descripción pobre llega al
            // laboratorio; una que no se promueve, no llega nunca.
            CodeText: order.CodeText ?? order.CodeDisplay ?? order.LoincCode ?? order.LocalCode ?? "Estudio de laboratorio",
            Priority: order.Priority,
            OccurrenceDateTime: order.OccurrenceDateTime,
            ReasonText: order.ReasonText,
            Note: order.Note,
            Requisition: order.Requisition,
            LocalCode: order.LocalCode,
            LocalSystem: order.LocalSystem);

        await hapi.UpdateLabOrderAsync(order.Id, request, ct);

        // Queda registrado. Una orden que llega al laboratorio tarde y por otro camino no puede ser
        // indistinguible de una que salió bien: si esto aparece seguido, hay algo que arreglar
        // aguas arriba y el log es la única forma de saberlo.
        db.AppAuditLog.Add(AppAuditLogEntry.Create(
            category: AuditCategory,
            action: "Promote:ServiceRequest",
            severity: AuditSeverity.Warning,
            actorUserId: null,
            organizationId: organizationId,
            target: $"ServiceRequest/{order.Id}",
            correlationId: null,
            remoteIp: null,
            payloadJson: System.Text.Json.JsonSerializer.Serialize(new
            {
                encounter = order.EncounterFhirId,
                requisition = order.Requisition,
                authoredOn = order.AuthoredOn,
                reason = "La consulta se cerró y la orden quedó en draft: el cliente no completó la promoción.",
            }),
            timeProvider: timeProvider));

        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Orden de laboratorio {Id} del encuentro {Encounter} promovida por reconciliación.",
            order.Id, order.EncounterFhirId);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Apagado normal.
        }
    }
}
