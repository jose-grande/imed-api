using IMed.Api.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Backfill idempotente del catálogo copy-on-create (rev 53): clona las plantillas
/// globales a toda organización existente que aún no tenga su catálogo propio.
/// Cierra la brecha para las orgs creadas antes del cambio de modelo. Corre al
/// startup, DESPUÉS de <see cref="LabCatalogSeeder"/>/<see cref="LabRuleSeeder"/>
/// (orden de registro), para que las globales ya existan. El bootstrap de una org
/// nueva ya aprovisiona en línea; esto solo recupera a las preexistentes.
/// </summary>
public sealed class LabCatalogProvisioningBackfill(
    IServiceScopeFactory scopeFactory,
    ILogger<LabCatalogProvisioningBackfill> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provisioning = scope.ServiceProvider.GetRequiredService<ILabCatalogProvisioning>();

            var orgIds = await db.OrganizationSubscriptions.AsNoTracking()
                .Select(x => x.OrganizationId)
                .ToListAsync(cancellationToken);

            var provisioned = 0;
            foreach (var orgId in orgIds)
            {
                try
                {
                    if (await provisioning.ProvisionOrganizationAsync(orgId, cancellationToken))
                    {
                        provisioned++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una org que falla no debe frenar al resto; el próximo arranque reintenta.
                    logger.LogError(ex, "Backfill del catálogo falló para org {OrgId}; se reintentará al próximo arranque.", orgId);
                }
            }

            if (provisioned > 0)
            {
                logger.LogInformation("Backfill del catálogo: aprovisionadas {Count} org(s) preexistentes.", provisioned);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LabCatalogProvisioningBackfill falló; las orgs preexistentes pueden quedar sin catálogo.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
