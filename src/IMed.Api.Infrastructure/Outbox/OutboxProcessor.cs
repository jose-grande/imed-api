using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Outbox;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Outbox;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Worker que consume <c>outbox_events</c> con <c>FOR UPDATE SKIP LOCKED</c>
/// (Postgres) — multi-instance ready desde el día 0. Cada tick toma hasta
/// <see cref="OutboxProcessorOptions.BatchSize"/> eventos elegibles
/// (<c>dispatched_at IS NULL AND next_attempt_at &lt;= now</c>), los procesa
/// con su <see cref="IOutboxDispatcher"/> correspondiente, y aplica backoff
/// exponencial sobre fallos transitorios.
///
/// <para>Modelo de error:</para>
/// <list type="bullet">
///   <item>Excepción transitoria → <c>MarkFailed</c> con backoff. Reintentará.</item>
///   <item><see cref="OutboxPermanentFailureException"/> → log error +
///         entrada de audit log con severity Error + el evento queda dispatched
///         (sin más retries) con <c>last_error</c> seteado.</item>
///   <item>Sin dispatcher para el <c>Type</c> → log error + skip (queda pendiente
///         hasta que se registre uno o se intervenga manualmente).</item>
/// </list>
///
/// <para>Configuración: sección <c>Outbox</c> en appsettings.</para>
/// </summary>
public sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<OutboxProcessorOptions> optionsMonitor,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OutboxProcessor iniciado.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var options = optionsMonitor.CurrentValue;
            if (!options.Enabled)
            {
                await DelayAsync(TimeSpan.FromSeconds(options.IdlePollSeconds), stoppingToken);
                continue;
            }

            int processed;
            try
            {
                processed = await ProcessBatchAsync(options, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "OutboxProcessor: error inesperado en el tick. Backoff 5s.");
                await DelayAsync(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }

            var pollInterval = processed > 0
                ? TimeSpan.FromSeconds(options.BusyPollSeconds)
                : TimeSpan.FromSeconds(options.IdlePollSeconds);
            await DelayAsync(pollInterval, stoppingToken);
        }

        logger.LogInformation("OutboxProcessor detenido.");
    }

    private async Task<int> ProcessBatchAsync(OutboxProcessorOptions options, CancellationToken cancellationToken)
    {
        // Cada batch corre en su propio scope para tener un DbContext limpio.
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var dispatchers = ResolveDispatchers(sp);

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        // FOR UPDATE SKIP LOCKED: si otra instancia toma estas filas, las skipea.
        // ORDER BY next_attempt_at: prioriza los más viejos. Limit = BatchSize.
        var now = timeProvider.GetUtcNow();
        var pending = await db.OutboxEvents
            .FromSqlRaw(
                """
                SELECT * FROM outbox_events
                WHERE dispatched_at IS NULL
                  AND next_attempt_at <= {0}
                ORDER BY next_attempt_at ASC
                LIMIT {1}
                FOR UPDATE SKIP LOCKED
                """,
                now, options.BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return 0;
        }

        var auditLog = sp.GetRequiredService<IAppAuditLog>();

        foreach (var ev in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DispatchOneAsync(ev, dispatchers, auditLog, options, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return pending.Count;
    }

    private async Task DispatchOneAsync(
        OutboxEvent ev,
        Dictionary<string, IOutboxDispatcher> dispatchers,
        IAppAuditLog auditLog,
        OutboxProcessorOptions options,
        CancellationToken cancellationToken)
    {
        if (!dispatchers.TryGetValue(ev.Type, out var dispatcher))
        {
            // Sin dispatcher: deja el evento pendiente con backoff largo y log.
            // No es OperationalException porque puede ser un código que aún no
            // desplegamos. El operador decide.
            logger.LogError(
                "OutboxProcessor: no hay IOutboxDispatcher para tipo '{Type}'. eventId={Id}",
                ev.Type, ev.Id);
            ev.MarkFailed(
                $"No dispatcher registered for type '{ev.Type}'.",
                ComputeBackoff(ev.AttemptCount + 1, options),
                timeProvider);
            return;
        }

        try
        {
            await dispatcher.DispatchAsync(ev.Payload, cancellationToken);
            ev.MarkDispatched(timeProvider);

            await auditLog.RecordAsync(AppAuditLogEntry.Create(
                category: "outbox",
                action: "dispatched",
                severity: AuditSeverity.Info,
                target: $"OutboxEvent:{ev.Id}",
                payloadJson: JsonSerializer.Serialize(new { ev.Type, ev.Aggregate, ev.AttemptCount }),
                timeProvider: timeProvider),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutboxPermanentFailureException ex)
        {
            logger.LogError(ex,
                "OutboxProcessor: falla PERMANENTE despachando '{Type}'. eventId={Id}",
                ev.Type, ev.Id);
            // Permanente: registramos el error y congelamos (sin borrar LastError).
            ev.MarkFailed(ex.Message, TimeSpan.Zero, timeProvider);
            ev.Freeze(timeProvider);

            await auditLog.RecordAsync(AppAuditLogEntry.Create(
                category: "outbox",
                action: "dispatch_failed_permanent",
                severity: AuditSeverity.Error,
                target: $"OutboxEvent:{ev.Id}",
                payloadJson: JsonSerializer.Serialize(new { ev.Type, ev.Aggregate, error = ex.Message }),
                timeProvider: timeProvider),
                cancellationToken);
        }
        catch (Exception ex)
        {
            var nextAttempt = ev.AttemptCount + 1;

            if (nextAttempt >= options.MaxAttempts)
            {
                logger.LogError(ex,
                    "OutboxProcessor: max attempts ({Max}) alcanzado para '{Type}'. eventId={Id}",
                    options.MaxAttempts, ev.Type, ev.Id);
                ev.MarkFailed(ex.Message, TimeSpan.Zero, timeProvider);
                ev.Freeze(timeProvider);

                await auditLog.RecordAsync(AppAuditLogEntry.Create(
                    category: "outbox",
                    action: "dispatch_failed_max_attempts",
                    severity: AuditSeverity.Error,
                    target: $"OutboxEvent:{ev.Id}",
                    payloadJson: JsonSerializer.Serialize(new { ev.Type, ev.Aggregate, attempts = nextAttempt, error = ex.Message }),
                    timeProvider: timeProvider),
                    cancellationToken);
                return;
            }

            var backoff = ComputeBackoff(nextAttempt, options);
            logger.LogWarning(ex,
                "OutboxProcessor: falla transitoria despachando '{Type}'. eventId={Id}, attempt={Attempt}, retry en {Backoff}s",
                ev.Type, ev.Id, nextAttempt, (int)backoff.TotalSeconds);
            ev.MarkFailed(ex.Message, backoff, timeProvider);
        }
    }

    private static TimeSpan ComputeBackoff(int attempt, OutboxProcessorOptions options)
    {
        // 5s → 10s → 20s → 40s → ... cap a BackoffMaxSeconds.
        // attempt es 1-based.
        var seconds = (long)options.BackoffBaseSeconds * (1L << Math.Min(attempt - 1, 30));
        if (seconds > options.BackoffMaxSeconds || seconds < 0)
        {
            seconds = options.BackoffMaxSeconds;
        }
        return TimeSpan.FromSeconds(seconds);
    }

    private static Dictionary<string, IOutboxDispatcher> ResolveDispatchers(IServiceProvider sp)
    {
        var all = sp.GetServices<IOutboxDispatcher>().ToArray();
        var dict = new Dictionary<string, IOutboxDispatcher>(StringComparer.Ordinal);
        foreach (var d in all)
        {
            if (!dict.TryAdd(d.EventType, d))
            {
                throw new InvalidOperationException(
                    $"Hay múltiples IOutboxDispatcher registrados para el tipo '{d.EventType}'.");
            }
        }
        return dict;
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Caller decide si propagar.
        }
    }
}
