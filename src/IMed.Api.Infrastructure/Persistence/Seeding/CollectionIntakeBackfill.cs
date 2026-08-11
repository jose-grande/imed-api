using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Lab;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Backfill de la convergencia ciclo-de-muestra ↔ flujo operativo.
///
/// <para>Las muestras que pasaron por las estaciones del rediseño <b>antes</b> de que existiera
/// <see cref="CollectionIntakeConvergence"/> quedaron con su contenedor en <c>Received</c> pero sin
/// fila en <c>order_intake</c>: invisibles para la worklist de Resultados, para el dashboard y para
/// el TAT, y con la captura de resultados rechazándolas por "la orden no fue recibida". Este
/// hosted service las recupera al arrancar.</para>
///
/// <para>Idempotente por partida doble: solo mira contenedores ya resueltos por QC, y la propia
/// convergencia salta las órdenes que ya tienen intake. Corre después de los seeders para que el
/// catálogo esté disponible.</para>
/// </summary>
public sealed class CollectionIntakeBackfill(
    IServiceScopeFactory scopeFactory,
    ILogger<CollectionIntakeBackfill> logger) : IHostedService
{
    /// <summary>Contenedores que ya pasaron por QC: son los que deberían tener intake.</summary>
    private static readonly ContainerStatus[] Resolved =
        [ContainerStatus.Received, ContainerStatus.Stored, ContainerStatus.Rejected];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var convergence = scope.ServiceProvider.GetRequiredService<CollectionIntakeConvergence>();

            var plans = await db.CollectionPlans
                .Include(p => p.Containers.OrderBy(c => c.Sequence))
                    .ThenInclude(c => c.Items)
                .Where(p => p.Containers.Any(c => Resolved.Contains(c.Status)))
                .ToListAsync(cancellationToken);

            var registered = 0;
            foreach (var plan in plans)
            {
                foreach (var container in plan.Containers.Where(c => Resolved.Contains(c.Status)))
                {
                    try
                    {
                        registered += await convergence.ConvergeAsync(
                            plan.OrganizationId,
                            container,
                            userId: null,
                            accepted: container.Status != ContainerStatus.Rejected,
                            rejectionReason: container.RejectionReasonCode,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Un contenedor que falla (orden borrada en HAPI, etc.) no frena al resto.
                        logger.LogWarning(
                            ex, "Backfill de convergencia falló para el contenedor {ContainerId}.", container.Id);
                    }
                }
            }

            if (registered > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Backfill de convergencia: {Count} orden(es) registradas en el flujo operativo.", registered);
            }

            await BackfillExamDescriptionsAsync(scope, db, cancellationToken);
            await BackfillReportAnchorsAsync(scope, db, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(
                ex, "CollectionIntakeBackfill falló; las muestras previas pueden no aparecer en Resultados.");
        }
    }

    /// <summary>
    /// Completa el examen/panel de los intakes creados antes de que se guardara ese snapshot.
    /// Sin él la worklist dice a quién pertenece la muestra pero no qué hay que resultar.
    /// </summary>
    private async Task BackfillExamDescriptionsAsync(
        AsyncServiceScope scope, AppDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.OrderIntakes
            .Where(x => x.ExamText == null)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var hapi = scope.ServiceProvider.GetRequiredService<IHapiFhirClient>();
        var described = 0;
        foreach (var intake in pending)
        {
            try
            {
                var order = await hapi.GetLabOrderAsync(
                    intake.ServiceRequestFhirId, $"org-{intake.OrganizationId}", cancellationToken);
                if (order is null || string.IsNullOrWhiteSpace(order.CodeText))
                {
                    continue;
                }
                intake.DescribeExam(order.LoincCode, order.CodeText);
                described++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex, "No se pudo describir el examen del intake {IntakeId}.", intake.Id);
            }
        }

        if (described > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Backfill de exámenes: {Count} intake(s) descritos.", described);
        }
    }

    /// <summary>
    /// Completa <c>order_intake.report_fhir_id</c> en las órdenes resultadas antes de que esa
    /// columna existiera.
    ///
    /// <para><b>Por qué hace falta:</b> la columna nació vacía con la migración, así que toda
    /// orden ya resultada quedaba con el ancla en <c>null</c>. La hoja de captura y la guarda
    /// contra doble guardado lo resuelven con una búsqueda en HAPI, pero <b>la worklist no</b>:
    /// resolverlo ahí sería una búsqueda por fila. El efecto visible era que una orden con
    /// resultados seguía ofreciendo "Ingresar resultados" — el enlace anunciaba lo contrario de
    /// lo que la pantalla iba a mostrar.</para>
    ///
    /// <para>Idempotente: solo toca filas sin ancla, y una vez llenas no vuelve a mirarlas.
    /// Agrupa por paciente para no repetir la misma búsqueda una vez por orden.</para>
    /// </summary>
    private async Task BackfillReportAnchorsAsync(
        AsyncServiceScope scope, AppDbContext db, CancellationToken cancellationToken)
    {
        var pending = await db.OrderIntakes
            .Where(x => x.ReportFhirId == null && x.Status != OrderIntakeStatus.Pending)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var hapi = scope.ServiceProvider.GetRequiredService<IHapiFhirClient>();
        var anchored = 0;
        foreach (var group in pending.GroupBy(x => new { x.OrganizationId, x.PatientFhirId }))
        {
            if (string.IsNullOrWhiteSpace(group.Key.PatientFhirId))
            {
                continue;
            }

            try
            {
                var reports = await hapi.SearchDiagnosticReportsByPatientAsync(
                    group.Key.PatientFhirId, $"org-{group.Key.OrganizationId}", cancellationToken);
                if (reports.Count == 0)
                {
                    continue;
                }

                foreach (var intake in group)
                {
                    var report = reports.FirstOrDefault(r => string.Equals(
                        r.ServiceRequestFhirId, intake.ServiceRequestFhirId, StringComparison.Ordinal));
                    if (report is null)
                    {
                        continue;
                    }
                    intake.AttachReport(report.Id);
                    anchored++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Un paciente que falla no frena al resto: sin ancla se sigue resolviendo por
                // búsqueda, que es el comportamiento previo.
                logger.LogWarning(
                    ex, "No se pudo anclar el informe de las órdenes del paciente {PatientId}.",
                    group.Key.PatientFhirId);
            }
        }

        if (anchored > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Backfill de informes: {Count} orden(es) ancladas.", anchored);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
