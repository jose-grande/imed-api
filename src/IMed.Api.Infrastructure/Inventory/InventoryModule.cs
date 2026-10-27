using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Application.Pharmacy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Inventory;

/// <summary>
/// Registro del módulo de inventario y farmacia (plan de salida a producción, Stream B). Vive en su
/// propio archivo para que el registro general de dependencias solo necesite una línea.
/// </summary>
public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddScoped<IInventoryUnitOfWork, InventoryUnitOfWork>();
        services.AddScoped<IStockLedger, StockLedger>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<IPrescriptionSource, HapiPrescriptionSource>();

        services.AddScoped<WarehouseProvisioner>();
        services.AddScoped<WarehouseResolver>();
        services.AddScoped<StockOutPlanner>();
        services.AddScoped<StockItemCommandHandler>();
        services.AddScoped<SearchStockItemsHandler>();
        services.AddScoped<WarehouseHandlers>();
        services.AddScoped<SupplierHandlers>();
        services.AddScoped<InventorySettingsHandlers>();
        services.AddScoped<GoodsReceiptHandlers>();
        services.AddScoped<StockMovementHandlers>();
        services.AddScoped<AdjustmentHandlers>();
        services.AddScoped<StockCountHandlers>();
        services.AddScoped<InventoryQueryHandlers>();
        services.AddScoped<InventoryReportHandlers>();

        services.AddScoped<DispenseReader>();
        services.AddScoped<PrescriptionQueueHandler>();
        services.AddScoped<DispensePrescriptionHandler>();
        services.AddScoped<ListDispensesHandler>();
        services.AddScoped<OverTheCounterSaleHandler>();

        // B6: la nota de crédito devuelve al inventario. Reemplaza la implementación vacía de la
        // Fase 0 (NoStockReturnPort) sin tocar su registro.
        services.Replace(ServiceDescriptor.Scoped<IStockReturnPort, StockReturnPort>());

        services.AddHostedService<InventoryBackfill>();
        return services;
    }
}

/// <summary>
/// Backfill de arranque del inventario (P7, B2): la bodega general de cada sede que todavía no la
/// tiene —sedes anteriores al módulo— y el cobrable de los artículos vendibles que nacieron antes
/// de que la organización configurara su régimen fiscal. Idempotente: en un arranque normal no
/// encuentra nada que hacer.
/// </summary>
public sealed class InventoryBackfill(IServiceScopeFactory scopes, ILogger<InventoryBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IInventoryRepository>();
            foreach (var org in await repo.ListOrganizationsWithLocationsAsync(stoppingToken))
            {
                using var porOrg = scopes.CreateScope();
                var sp = porOrg.ServiceProvider;
                try
                {
                    await sp.GetRequiredService<WarehouseProvisioner>().EnsureGeneralWarehousesAsync(org, stoppingToken);
                    var items = sp.GetRequiredService<StockItemCommandHandler>();
                    foreach (var item in await sp.GetRequiredService<IInventoryRepository>()
                                 .ListSellableWithoutBillableAsync(org, stoppingToken))
                    {
                        await items.AsegurarCobrableAsync(org, item, stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Una organización con datos raros no frena el arranque ni a las demás: lo
                    // perezoso la recupera cuando alguien abra su inventario.
                    logger.LogWarning(ex, "Backfill de inventario falló para la organización {OrgId}.", org);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Backfill de inventario no pudo correr.");
        }
    }
}
