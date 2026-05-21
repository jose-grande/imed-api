using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Slice mock del catálogo SNOMED CT de alergenos comunes para preclínica
/// (Épica J — D-Hito2-08). ~25 entries cubren el grueso de alergias prevalentes
/// en consulta ambulatoria HN: medicamentos comunes, alimentos, picaduras,
/// polen/ácaros. La UI ofrece también input libre, así que esto es typeahead
/// asistido — no cubre todo el universo. Si el set crece a cientos, migrar a
/// <c>INamedTerminologyCatalogReader</c> + tsv como ICD-11/LOINC.
/// </summary>
public sealed class AllergySeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<AllergySeeder> logger) : IHostedService
{
    private static readonly (string Code, string DisplayEs, string Category)[] Seed =
    [
        // Medicamentos
        ("294505008", "Alergia a penicilina", "medication"),
        ("294530008", "Alergia a sulfonamidas", "medication"),
        ("293584003", "Alergia a aspirina", "medication"),
        ("294423003", "Alergia a antiinflamatorios no esteroideos (AINEs)", "medication"),
        ("294500001", "Alergia a cefalosporinas", "medication"),
        ("294522006", "Alergia a tetraciclinas", "medication"),
        ("294479006", "Alergia a yodo", "medication"),
        // Alimentos
        ("91935009",  "Alergia al maní", "food"),
        ("91934008",  "Alergia a mariscos", "food"),
        ("417532002", "Alergia a frutos secos", "food"),
        ("300913006", "Alergia a la leche", "food"),
        ("91931000",  "Alergia al huevo", "food"),
        ("300912001", "Alergia al trigo / gluten", "food"),
        ("418471000", "Alergia a la soya", "food"),
        ("417918005", "Alergia al pescado", "food"),
        // Ambientales
        ("418689008", "Alergia al polen de gramíneas", "environment"),
        ("232347008", "Alergia a ácaros del polvo", "environment"),
        ("232346004", "Alergia al pelo de gato", "environment"),
        ("232350006", "Alergia al pelo de perro", "environment"),
        ("418002000", "Alergia al látex", "environment"),
        ("232352003", "Alergia a hongos / esporas de moho", "environment"),
        // Picaduras / venenos
        ("300916003", "Alergia a picadura de abeja", "biologic"),
        ("418217005", "Alergia a picadura de avispa", "biologic"),
        // Genéricos / fallback
        ("418634005", "Alergia a animales (no especificada)", "environment"),
        ("106190000", "Alergia no especificada", "other"),
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
                existingCount = await db.AllergyCatalog.CountAsync(cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla allergy_catalog aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                existingCount = await db.AllergyCatalog.CountAsync(cancellationToken);
            }

            if (existingCount > 0)
            {
                logger.LogDebug("Allergy catalog ya tiene {Count} entradas; skip seed.", existingCount);
                return;
            }

            var entities = Seed.Select(s => AllergyCatalogEntry.Create(s.Code, s.DisplayEs, s.Category)).ToArray();
            db.AllergyCatalog.AddRange(entities);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Allergy seed cargado con {Count} alergenos.", entities.Length);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AllergySeeder falló; el catálogo de alergenos puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
