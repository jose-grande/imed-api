using IMed.Api.Domain.Catalogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence.Seeding;

/// <summary>
/// Slice mock del catálogo WHO ATC con ~50 medicamentos frecuentes en
/// consulta ambulatoria HN (Épica K1 — D-Hito2-05). Cuando el dueño consiga
/// el catálogo oficial HN (o RxNorm), reemplazar este seeder por un
/// importer del CSV correspondiente — el shape de la entity ya soporta los
/// mismos campos.
/// </summary>
public sealed class MedicationSeeder(
    IServiceScopeFactory scopeFactory,
    ILogger<MedicationSeeder> logger) : IHostedService
{
    private static readonly (string Atc, string Generic, string? Commercial, string? Presentation, string Category)[] Seed =
    [
        // Analgésicos / antiinflamatorios
        ("N02BE01", "Paracetamol", "Acetaminofén / Tempra", "tab 500 mg", "analgesico"),
        ("N02BE01", "Paracetamol", "Acetaminofén jarabe", "susp 100 mg/5 ml", "analgesico"),
        ("M01AE01", "Ibuprofeno", "Advil / Motrin", "tab 400 mg", "analgesico"),
        ("M01AE01", "Ibuprofeno", "Ibuprofeno susp.", "susp 100 mg/5 ml", "analgesico"),
        ("N02BB02", "Metamizol sódico", "Dipirona / Novalgina", "tab 500 mg", "analgesico"),
        ("M01AB05", "Diclofenaco", "Voltaren", "tab 50 mg", "analgesico"),
        ("M01AB05", "Diclofenaco", "Voltaren amp.", "amp 75 mg/3 ml IM", "analgesico"),
        ("N02BA01", "Ácido acetilsalicílico", "Aspirina", "tab 100 mg", "analgesico"),

        // Antibióticos
        ("J01CA04", "Amoxicilina", "Amoxil", "cáps 500 mg", "antibiotico"),
        ("J01CA04", "Amoxicilina", "Amoxicilina susp.", "susp 250 mg/5 ml", "antibiotico"),
        ("J01CR02", "Amoxicilina + ácido clavulánico", "Augmentin", "tab 875/125 mg", "antibiotico"),
        ("J01FA10", "Azitromicina", "Zithromax", "tab 500 mg", "antibiotico"),
        ("J01FA09", "Claritromicina", "Klaricid", "tab 500 mg", "antibiotico"),
        ("J01EE01", "Sulfametoxazol + trimetoprima", "Bactrim / TMP-SMX", "tab 800/160 mg", "antibiotico"),
        ("J01MA02", "Ciprofloxacino", "Cipro", "tab 500 mg", "antibiotico"),
        ("J01MA12", "Levofloxacino", "Levaquin", "tab 500 mg", "antibiotico"),
        ("J01AA02", "Doxiciclina", "Vibramycin", "tab 100 mg", "antibiotico"),
        ("J01XD01", "Metronidazol", "Flagyl", "tab 500 mg", "antibiotico"),
        ("J01DD08", "Cefixima", "Suprax", "tab 400 mg", "antibiotico"),
        ("J01CE02", "Penicilina V", "Penicilina oral", "tab 500 mg", "antibiotico"),

        // Antihipertensivos / cardiovasculares
        ("C09AA02", "Enalapril", "Renitec", "tab 10 mg", "antihipertensivo"),
        ("C09AA03", "Lisinopril", "Prinivil / Zestril", "tab 10 mg", "antihipertensivo"),
        ("C09CA01", "Losartán", "Cozaar", "tab 50 mg", "antihipertensivo"),
        ("C09CA06", "Valsartán", "Diovan", "tab 80 mg", "antihipertensivo"),
        ("C08CA01", "Amlodipino", "Norvasc", "tab 5 mg", "antihipertensivo"),
        ("C07AB07", "Bisoprolol", "Concor", "tab 5 mg", "antihipertensivo"),
        ("C07AB02", "Metoprolol", "Lopressor", "tab 50 mg", "antihipertensivo"),
        ("C03CA01", "Furosemida", "Lasix", "tab 40 mg", "antihipertensivo"),
        ("C03BA11", "Hidroclorotiazida", "HCTZ", "tab 25 mg", "antihipertensivo"),

        // Gastrointestinal
        ("A02BC01", "Omeprazol", "Losec", "cáps 20 mg", "gastrointestinal"),
        ("A02BC03", "Lansoprazol", "Prevacid", "cáps 30 mg", "gastrointestinal"),
        ("A02BC05", "Esomeprazol", "Nexium", "cáps 40 mg", "gastrointestinal"),
        ("A02BA02", "Ranitidina", "Zantac", "tab 150 mg", "gastrointestinal"),
        ("A03FA01", "Metoclopramida", "Reglan / Plasil", "tab 10 mg", "gastrointestinal"),
        ("A03FA03", "Domperidona", "Motilium", "tab 10 mg", "gastrointestinal"),
        ("A04AA01", "Ondansetrón", "Zofran", "tab 4 mg", "gastrointestinal"),
        ("A07AA02", "Nistatina", "Mycostatin", "susp 100,000 UI/ml", "gastrointestinal"),
        ("A07DA03", "Loperamida", "Imodium", "cáps 2 mg", "gastrointestinal"),

        // Diabetes / endocrino
        ("A10BA02", "Metformina", "Glucophage", "tab 850 mg", "endocrino"),
        ("A10BB12", "Glimepirida", "Amaryl", "tab 4 mg", "endocrino"),
        ("A10AB01", "Insulina humana regular", "Humulin R", "vial 100 UI/ml", "endocrino"),
        ("A10AC01", "Insulina humana NPH", "Humulin N", "vial 100 UI/ml", "endocrino"),
        ("H03AA01", "Levotiroxina", "Synthroid / Eutirox", "tab 50 mcg", "endocrino"),

        // Respiratorio / antihistamínicos
        ("R06AE07", "Cetirizina", "Zyrtec", "tab 10 mg", "antihistaminico"),
        ("R06AX27", "Desloratadina", "Aerius", "tab 5 mg", "antihistaminico"),
        ("R06AX13", "Loratadina", "Claritin", "tab 10 mg", "antihistaminico"),
        ("R03AC02", "Salbutamol inhalador", "Ventolin", "inhal 100 mcg/dosis", "respiratorio"),
        ("R03BA02", "Budesonida inhalador", "Pulmicort", "inhal 200 mcg/dosis", "respiratorio"),

        // Vitaminas / suplementos
        ("A11CC01", "Vitamina D3 (colecalciferol)", "—", "tab 1000 UI", "suplemento"),
        ("B03BA01", "Vitamina B12 (cianocobalamina)", "—", "tab 1000 mcg", "suplemento"),
        ("B03AA02", "Sulfato ferroso", "—", "tab 200 mg", "suplemento"),
        ("A12AA04", "Carbonato de calcio", "—", "tab 500 mg", "suplemento"),
        ("B03BB01", "Ácido fólico", "—", "tab 5 mg", "suplemento"),
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
                existingCount = await db.MedicationCatalog.CountAsync(cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Tabla medication_catalog aún no existe; ejecutando migrate y reintentando.");
                await db.Database.MigrateAsync(cancellationToken);
                existingCount = await db.MedicationCatalog.CountAsync(cancellationToken);
            }

            if (existingCount > 0)
            {
                logger.LogDebug("Medication catalog ya tiene {Count} entradas; skip seed.", existingCount);
                return;
            }

            // Filtramos duplicados por (AtcCode, Presentation): la PK es solo
            // AtcCode pero queremos mantener variantes de presentación. Por
            // ahora un medicamento aparece varias veces si tiene tab + susp;
            // dedup post-piloto: agregar presentation a la PK compuesta.
            // Para no romper el seed con un PK conflict, conservamos solo
            // la primera presentación por código ATC.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var entities = new List<MedicationCatalogEntry>();
            foreach (var s in Seed)
            {
                if (!seen.Add(s.Atc))
                {
                    continue;
                }
                entities.Add(MedicationCatalogEntry.Create(s.Atc, s.Generic, s.Commercial, s.Presentation, s.Category));
            }

            db.MedicationCatalog.AddRange(entities);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Medication seed cargado con {Count} medicamentos.", entities.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "MedicationSeeder falló; el catálogo de medicamentos puede estar incompleto.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
