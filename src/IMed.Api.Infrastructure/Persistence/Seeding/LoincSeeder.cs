using IMed.Api.Domain.Terminology;
using IMed.Api.Infrastructure.Terminology.Importers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seeder del catálogo LOINC. Recibe el <c>BaseDir</c> del release LOINC
/// (configurado en <c>Catalogs:Loinc:BaseDir</c>) y delega al
/// <see cref="LoincStandardImporter"/> que resuelve los archivos hijos.
/// Idempotente: skip si la tabla ya tiene rows.
/// </summary>
public sealed class LoincSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<LoincSeeder> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            int existingCount;
            try
            {
                existingCount = await db.LoincCatalog.CountAsync(cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla loinc_catalog aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                existingCount = await db.LoincCatalog.CountAsync(cancellationToken);
            }

            if (existingCount > 0)
            {
                logger.LogDebug("LOINC catalog ya tiene {Count} entradas; skip seed.", existingCount);
                return;
            }

            var baseDir = configuration["Catalogs:Loinc:BaseDir"];
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                logger.LogWarning(
                    "Catalogs:Loinc:BaseDir no configurado; LOINC catalog queda vacío. " +
                    "Búsquedas de labs devolverán resultados vacíos hasta que se configure y reseed.");
                return;
            }

            if (!Directory.Exists(baseDir))
            {
                logger.LogWarning(
                    "Directorio LOINC no encontrado en {Path}; catálogo queda vacío. Verificar path o descomprimir el ZIP de LOINC.",
                    baseDir);
                return;
            }

            var importers = scope.ServiceProvider.GetServices<ITerminologyCatalogImporter>();
            var importer = importers.FirstOrDefault(i =>
                i.CodeSystem == CodeSystems.Loinc &&
                i.SourceFormat == "loinc-standard");

            if (importer is null)
            {
                logger.LogError("No hay ITerminologyCatalogImporter registrado para LOINC / loinc-standard.");
                return;
            }

            logger.LogInformation("Cargando LOINC desde {Path}...", baseDir);
            var result = await importer.ImportAsync(baseDir, cancellationToken);

            logger.LogInformation(
                "LOINC seed cargado: inserted={Inserted}, skipped={Skipped}, errors={Errors}.",
                result.Inserted, result.Skipped, result.Errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "LoincSeeder falló; el catálogo LOINC puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
