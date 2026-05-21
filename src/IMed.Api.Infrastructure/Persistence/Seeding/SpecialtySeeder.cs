using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Seeder del catálogo de especialidades médicas (SNOMED CT). Solo carga si la
/// tabla está vacía. Idempotente. Hito 2 puede extender o reemplazar con el
/// catálogo SNOMED ES completo.
/// </summary>
public sealed class SpecialtySeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<SpecialtySeeder> logger) : IHostedService
{
    /// <summary>
    /// ~15 especialidades comunes en consulta ambulatoria centroamericana.
    /// Codes SNOMED CT (US/ES). Display en español de Honduras.
    /// </summary>
    private static readonly (string Code, string DisplayEs)[] Seed =
    [
        ("394814009", "Medicina general y de familia"),
        ("394537008", "Pediatría"),
        ("394585009", "Obstetricia y ginecología"),
        ("394579002", "Cardiología"),
        ("394802001", "Medicina interna"),
        ("394586005", "Oftalmología"),
        ("394605006", "Cirugía plástica"),
        ("394609007", "Cirugía general"),
        ("394914008", "Radiología"),
        ("394913002", "Otorrinolaringología"),
        ("394582007", "Dermatología"),
        ("394583002", "Endocrinología"),
        ("394584008", "Gastroenterología"),
        ("394591006", "Psiquiatría"),
        ("394801008", "Traumatología y ortopedia"),
        ("394589003", "Neurología"),
        ("394806003", "Urología"),
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            int existingCount;
            try
            {
                existingCount = await db.SpecialtyCatalog.CountAsync(cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla specialty_catalog aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                existingCount = await db.SpecialtyCatalog.CountAsync(cancellationToken);
            }

            if (existingCount > 0)
            {
                logger.LogDebug("Specialty catalog ya tiene {Count} entradas; skip seed.", existingCount);
                return;
            }

            var entities = Seed.Select(s => SpecialtyCode.Create(s.Code, s.DisplayEs)).ToArray();
            db.SpecialtyCatalog.AddRange(entities);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Specialty seed cargado con {Count} especialidades.", entities.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SpecialtySeeder falló; el catálogo de especialidades puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
