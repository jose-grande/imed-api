using IMed.Api.Application.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Backfill del alta automática de cobrables (A2, A6) para las organizaciones que ya existían:
/// consultas de las especialidades de su equipo y cobrables de su laboratorio, sin precio.
/// Idempotente; corre después de <see cref="LabCatalogProvisioningBackfill"/> (orden de registro)
/// para que cada organización ya tenga sus pruebas.
///
/// <para>Corre en segundo plano y no en <c>StartAsync</c>: consulta HAPI por cada organización, y
/// el arranque de la API no puede depender de que HAPI conteste.</para>
/// </summary>
public sealed class BillableCatalogBackfill(
    IServiceScopeFactory scopeFactory,
    ILogger<BillableCatalogBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            List<Guid> orgIds;
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                // Solo las que tienen régimen fiscal: sin tributos no hay cobrable posible.
                orgIds = await db.TaxCodes.AsNoTracking()
                    .Select(x => x.OrganizationId).Distinct().ToListAsync(stoppingToken);
            }

            foreach (var orgId in orgIds)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<OrganizationCatalogReconciler>()
                        .ReconcileAsync(orgId, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Backfill de cobrables falló para {Org}; se reintenta al próximo arranque.", orgId);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "BillableCatalogBackfill falló.");
        }
    }
}
