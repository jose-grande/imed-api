using IMed.Api.Domain.Terminology;
using IMed.Api.Infrastructure.Terminology.Importers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seeder del catálogo ICD-11. Carga el TSV WHO Simple Tabulation desde el path
/// configurado en <c>Catalogs:Icd11SourcePath</c>. Idempotente: skip si la tabla
/// ya tiene rows.
///
/// <para>Si el path no está configurado o el archivo no existe, log warning y
/// no-op — permite arrancar imed-api en entornos sin el catálogo (ej. CI básico,
/// dev en máquina sin haber descomprimido el ZIP) sin crashear el host.</para>
/// </summary>
public sealed class Icd11Seeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<Icd11Seeder> logger) : IHostedService
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
                existingCount = await db.Icd11Catalog.CountAsync(cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla icd11_catalog aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                existingCount = await db.Icd11Catalog.CountAsync(cancellationToken);
            }

            if (existingCount > 0)
            {
                logger.LogDebug("ICD-11 catalog ya tiene {Count} entradas; skip seed.", existingCount);
                return;
            }

            var sourcePath = configuration["Catalogs:Icd11SourcePath"];
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                logger.LogWarning(
                    "Catalogs:Icd11SourcePath no configurado; ICD-11 catalog queda vacío. " +
                    "Búsquedas de diagnósticos devolverán resultados vacíos hasta que se configure y reseed.");
                return;
            }

            if (!File.Exists(sourcePath))
            {
                logger.LogWarning(
                    "Archivo ICD-11 no encontrado en {Path}; catálogo queda vacío. Verificar path o descomprimir el ZIP del WHO.",
                    sourcePath);
                return;
            }

            // Resolver el importer correcto por (CodeSystem, SourceFormat).
            // Hoy hay un solo importer ICD-11 (WHO Simple Tabulation). Cuando
            // aparezcan otros (ej: WHO Foundation API JSON), agregar un campo
            // de config Catalogs:Icd11SourceFormat para seleccionar.
            var importers = scope.ServiceProvider.GetServices<ITerminologyCatalogImporter>();
            var importer = importers.FirstOrDefault(i =>
                i.CodeSystem == CodeSystems.Icd11 &&
                i.SourceFormat == "who-simple-tabulation");

            if (importer is null)
            {
                logger.LogError("No hay ITerminologyCatalogImporter registrado para ICD-11 / who-simple-tabulation.");
                return;
            }

            logger.LogInformation("Cargando ICD-11 desde {Path}...", sourcePath);
            var result = await importer.ImportAsync(sourcePath, cancellationToken);

            logger.LogInformation(
                "ICD-11 seed cargado: inserted={Inserted}, skipped={Skipped}, errors={Errors}.",
                result.Inserted, result.Skipped, result.Errors);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Icd11Seeder falló; el catálogo ICD-11 puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
