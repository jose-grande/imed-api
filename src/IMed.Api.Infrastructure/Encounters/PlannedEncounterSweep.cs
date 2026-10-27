using System.Globalization;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Encounters;

/// <summary>Barrido de encuentros de preclínica huérfanos. Sección <c>PlannedEncounterSweep</c>.</summary>
public sealed class PlannedEncounterSweepOptions
{
    public const string SectionName = "PlannedEncounterSweep";

    public bool Enabled { get; set; } = true;

    /// <summary>Cada cuánto se corre. No hay apuro: lo que busca es de un día anterior.</summary>
    public int PollSeconds { get; set; } = 1800;

    public int MaxPerRun { get; set; } = 200;
}

/// <summary>
/// Cancela los encuentros <c>planned</c> que la preclínica abrió en un día anterior y que nadie
/// atendió (Épica P, pendiente de P-1).
///
/// <para><b>De dónde salen.</b> Desde P-D2 la preclínica abre el encuentro de la cita en
/// <c>planned</c> y el médico lo pasa a <c>in-progress</c> al atender. Si el paciente se va después
/// de la preclínica, el encuentro queda <c>planned</c> para siempre: aparece en la ficha como
/// "Con preclínica" y en cualquier conteo de consultas pendientes. Si la cita se marca "No vino",
/// la pantalla lo cancela en el momento; esto cubre el caso en que nadie toca la cita.</para>
///
/// <para><b>"Un día anterior" es en el día de la clínica</b>, no en UTC (<see cref="IClinicClock"/>):
/// en Honduras el día UTC cambia a las 18:00, y cancelar a esa hora un encuentro de las 17:00 sería
/// cancelar una consulta de la misma tarde.</para>
///
/// <para>Un encuentro sin fecha de actualización <b>no se toca</b>: a diferencia del barrido de
/// órdenes —donde equivocarse es promover algo que igual debía salir—, acá equivocarse es cancelar
/// una consulta viva. Cada cancelación lleva <c>ifMatch</c>: si el médico lo abrió justo en ese
/// momento, la escritura falla y la consulta sigue. Y queda en <c>app_audit_log</c>.</para>
/// </summary>
public sealed class PlannedEncounterSweep(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<PlannedEncounterSweepOptions> optionsMonitor,
    IClinicClock clinicClock,
    TimeProvider timeProvider,
    ILogger<PlannedEncounterSweep> logger) : BackgroundService
{
    public const string AuditCategory = "encounter_reconciliation";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogError(ex, "PlannedEncounterSweep: error en el barrido. Se reintenta en la próxima vuelta.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(60, options.PollSeconds)), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Un barrido completo. Público para que las pruebas no dependan del temporizador.</summary>
    public async Task<int> RunOnceAsync(PlannedEncounterSweepOptions options, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hapi = scope.ServiceProvider.GetRequiredService<IHapiFhirClient>();

        var organizations = await db.OrganizationSubscriptions.AsNoTracking()
            .Select(x => x.OrganizationId)
            .Distinct()
            .ToListAsync(ct);

        var today = clinicClock.Today();
        var cancelled = 0;
        foreach (var orgId in organizations)
        {
            if (cancelled >= options.MaxPerRun || ct.IsCancellationRequested)
            {
                break;
            }
            cancelled += await SweepOrganizationAsync(db, hapi, orgId, today, options.MaxPerRun - cancelled, ct);
        }

        if (cancelled > 0)
        {
            logger.LogInformation(
                "PlannedEncounterSweep: {Count} encuentro(s) de preclínica sin atender se cancelaron.", cancelled);
        }
        return cancelled;
    }

    private async Task<int> SweepOrganizationAsync(
        AppDbContext db, IHapiFhirClient hapi, Guid organizationId, DateOnly today, int budget, CancellationToken ct)
    {
        var partition = $"org-{organizationId}";
        var planned = await hapi.SearchResourcesAsync("Encounter", "status=planned&_count=200", partition, ct);

        var stale = planned
            .Where(e => LastUpdated(e) is { } at && clinicClock.DateOf(at) < today)
            .Take(budget)
            .ToList();

        var cancelled = 0;
        foreach (var encounter in stale)
        {
            var id = (string)encounter["id"]!;
            var version = (string?)encounter["meta"]?["versionId"];
            var updated = (JsonObject)encounter.DeepClone();
            updated.Remove("meta");
            updated["status"] = "cancelled";

            var request = new JsonObject { ["method"] = "PUT", ["url"] = $"Encounter/{id}" };
            if (version is not null)
            {
                request["ifMatch"] = $"W/\"{version}\"";
            }
            try
            {
                await hapi.ExecuteTransactionAsync(new JsonObject
                {
                    ["resourceType"] = "Bundle",
                    ["type"] = "transaction",
                    ["entry"] = new JsonArray(new JsonObject
                    {
                        ["fullUrl"] = $"urn:uuid:{Guid.NewGuid()}",
                        ["resource"] = updated,
                        ["request"] = request,
                    }),
                }, partition, ct);
            }
            catch (FhirVersionConflictException)
            {
                // Alguien lo tocó entre la lectura y la escritura —probablemente el médico al
                // atender—. No se insiste: si sigue planned, la próxima vuelta lo vuelve a evaluar.
                continue;
            }

            db.AppAuditLog.Add(AppAuditLogEntry.Create(
                category: AuditCategory,
                action: "Cancel:Encounter",
                severity: AuditSeverity.Info,
                organizationId: organizationId,
                target: $"Encounter/{id}",
                payloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    appointment = (string?)encounter["appointment"]?[0]?["reference"],
                    lastUpdated = (string?)encounter["meta"]?["lastUpdated"],
                    reason = "Preclínica de un día anterior sin atención médica.",
                }),
                timeProvider: timeProvider));
            await db.SaveChangesAsync(ct);
            cancelled++;
        }
        return cancelled;
    }

    private static DateTimeOffset? LastUpdated(JsonObject encounter) =>
        DateTimeOffset.TryParse(
            (string?)encounter["meta"]?["lastUpdated"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
