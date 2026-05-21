using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Terminology;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Terminology.Importers;

/// <summary>
/// Importer del catálogo LOINC desde el release standard (directorio multi-archivo).
/// Spec: <a href="https://loinc.org/downloads/loinc-table/">LOINC Table downloads</a>.
/// License: LOINC license (free + atribución, ver README de imed-api).
///
/// <para>Recibe el directorio raíz del release (ej. <c>Loinc_2.82</c>). Carga:</para>
/// <list type="bullet">
///   <item><c>{baseDir}/LoincTable/Loinc.csv</c> — main file (~98k filas, filtra <c>STATUS=ACTIVE</c>).</item>
///   <item><c>{baseDir}/AccessoryFiles/LinguisticVariants/{primary}.csv</c> — traducción primaria (default <c>esMX28LinguisticVariant.csv</c>).</item>
///   <item>Variants secundarios opcionales (ej. <c>esES12</c>) cargados como <c>designations</c> en Properties JSONB.</item>
/// </list>
///
/// <para>JOIN en memoria por <c>LOINC_NUM</c>. Códigos sin traducción al ES hacen
/// fallback al inglés (LONG_COMMON_NAME) con flag <c>properties["translation-pending"] = true</c>.</para>
/// </summary>
internal sealed class LoincStandardImporter(
    AppDbContext db,
    IConfiguration configuration,
    ILogger<LoincStandardImporter> logger) : ITerminologyCatalogImporter
{
    public string CodeSystem => CodeSystems.Loinc;

    public string SourceFormat => "loinc-standard";

    private const int BatchSize = 1000;

    /// <summary>
    /// JSON con UnsafeRelaxedJsonEscaping para preservar UTF-8 (España, función,
    /// etc.) en la columna JSONB. Sin esto JsonSerializer.Serialize escapa los
    /// no-ASCII a <c>ñ</c> y los consumers ven texto ilegible.
    /// </summary>
    private static readonly JsonSerializerOptions DesignationsJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task<ImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var primaryVariantFile = configuration["Catalogs:Loinc:PrimaryVariant"] ?? "esMX28LinguisticVariant.csv";
        var additionalVariants = configuration.GetSection("Catalogs:Loinc:AdditionalVariants")
            .Get<string[]>() ?? Array.Empty<string>();

        var mainPath = Path.Combine(sourcePath, "LoincTable", "Loinc.csv");
        var primaryVariantPath = Path.Combine(sourcePath, "AccessoryFiles", "LinguisticVariants", primaryVariantFile);

        if (!File.Exists(mainPath))
        {
            logger.LogError("LOINC main file no encontrado en {Path}", mainPath);
            return ImportResult.Empty;
        }

        var mainRows = await LoadMainAsync(mainPath, cancellationToken);
        logger.LogInformation("LOINC main cargado: {Count} filas ACTIVE.", mainRows.Count);

        // Variant primario: traducción ES default. Si no existe el archivo, no
        // bloqueamos — todo va con display_es=display_en y flag translation-pending.
        var primaryVariant = File.Exists(primaryVariantPath)
            ? await LoadLinguisticVariantAsync(primaryVariantPath, cancellationToken)
            : new Dictionary<string, LinguisticVariantRow>(StringComparer.Ordinal);
        if (!File.Exists(primaryVariantPath))
        {
            logger.LogWarning("LOINC linguistic variant primario no encontrado en {Path}; todos los códigos quedarán en inglés.", primaryVariantPath);
        }
        else
        {
            logger.LogInformation("LOINC variant primario ({File}) cargado: {Count} traducciones.", primaryVariantFile, primaryVariant.Count);
        }

        // Variants adicionales: cargados solo como designations alternativas en Properties.
        var additionalDesignations = new Dictionary<string, List<DesignationEntry>>(StringComparer.Ordinal);
        foreach (var variantFile in additionalVariants)
        {
            var variantPath = Path.Combine(sourcePath, "AccessoryFiles", "LinguisticVariants", variantFile);
            if (!File.Exists(variantPath))
            {
                logger.LogWarning("Variant adicional {File} no encontrado, skip.", variantFile);
                continue;
            }
            var langTag = ExtractLanguageTag(variantFile);
            var rows = await LoadLinguisticVariantAsync(variantPath, cancellationToken);
            foreach (var (code, lv) in rows)
            {
                if (!additionalDesignations.TryGetValue(code, out var list))
                {
                    list = new List<DesignationEntry>();
                    additionalDesignations[code] = list;
                }
                list.Add(new DesignationEntry(langTag, lv.LongCommonName));
            }
            logger.LogInformation("Variant adicional ({File}, lang={Lang}) cargado: {Count} designations.", variantFile, langTag, rows.Count);
        }

        // Materializar entities y bulk-insert por batches.
        var inserted = 0;
        var skipped = 0;
        var batch = new List<LoincCode>(BatchSize);

        foreach (var (code, main) in mainRows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hasTranslation = primaryVariant.TryGetValue(code, out var lv);
            var displayEs = hasTranslation && !string.IsNullOrWhiteSpace(lv!.LongCommonName)
                ? lv.LongCommonName!
                : main.LongCommonName;

            if (string.IsNullOrWhiteSpace(displayEs))
            {
                skipped++;
                continue;
            }

            var props = BuildProperties(main, hasTranslation);
            if (additionalDesignations.TryGetValue(code, out var designations) && designations.Count > 0)
            {
                // Serializamos como JSON-array dentro del JSONB para no inflar el shape.
                // Format: properties["designations"] = "[{\"lang\":\"es-ES\",\"value\":\"...\"}, ...]"
                props["designations"] = JsonSerializer.Serialize(
                    designations.Select(d => new { lang = d.Language, value = d.Value }).ToArray(),
                    DesignationsJsonOptions);
            }

            var entity = LoincCode.Create(
                code: code,
                displayEs: displayEs,
                displayEn: string.IsNullOrWhiteSpace(main.LongCommonName) ? null : main.LongCommonName,
                properties: props,
                active: true);

            batch.Add(entity);
            if (batch.Count >= BatchSize)
            {
                inserted += await FlushBatchAsync(batch, cancellationToken);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            inserted += await FlushBatchAsync(batch, cancellationToken);
        }

        return new ImportResult(inserted, skipped, 0);
    }

    private static async Task<Dictionary<string, MainRow>> LoadMainAsync(string path, CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
        };
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        var rows = new Dictionary<string, MainRow>(StringComparer.Ordinal);
        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var status = csv.GetField<string>("STATUS");
            if (!string.Equals(status, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var code = csv.GetField<string>("LOINC_NUM");
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            rows[code.Trim()] = new MainRow(
                LongCommonName: csv.GetField<string>("LONG_COMMON_NAME") ?? string.Empty,
                ShortName: csv.GetField<string>("SHORTNAME"),
                Class: csv.GetField<string>("CLASS"),
                ClassType: csv.GetField<string>("CLASSTYPE"),
                Property: csv.GetField<string>("PROPERTY"),
                Scale: csv.GetField<string>("SCALE_TYP"),
                System_: csv.GetField<string>("SYSTEM"),
                TimeAspect: csv.GetField<string>("TIME_ASPCT"),
                Method: csv.GetField<string>("METHOD_TYP"),
                IsPanel: csv.GetField<string>("PanelType"),
                OrderObs: csv.GetField<string>("ORDER_OBS"));
        }
        return rows;
    }

    private static async Task<Dictionary<string, LinguisticVariantRow>> LoadLinguisticVariantAsync(
        string path, CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
        };
        await using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, config);
        await csv.ReadAsync();
        csv.ReadHeader();

        var rows = new Dictionary<string, LinguisticVariantRow>(StringComparer.Ordinal);
        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            var code = csv.GetField<string>("LOINC_NUM");
            if (string.IsNullOrWhiteSpace(code))
            {
                continue;
            }
            rows[code.Trim()] = new LinguisticVariantRow(
                LongCommonName: csv.GetField<string>("LONG_COMMON_NAME"),
                LinguisticVariantDisplayName: csv.GetField<string>("LinguisticVariantDisplayName"));
        }
        return rows;
    }

    private static Dictionary<string, string> BuildProperties(MainRow main, bool hasTranslation)
    {
        var props = new Dictionary<string, string>();
        AddIf(props, "class", main.Class);
        AddIf(props, "class-type", main.ClassType);
        AddIf(props, "property", main.Property);
        AddIf(props, "scale-type", main.Scale);
        AddIf(props, "system", main.System_);
        AddIf(props, "time-aspect", main.TimeAspect);
        AddIf(props, "method-type", main.Method);
        AddIf(props, "short-name", main.ShortName);
        AddIf(props, "is-panel", string.IsNullOrEmpty(main.IsPanel) ? null : main.IsPanel);
        AddIf(props, "order-obs", main.OrderObs);
        if (!hasTranslation)
        {
            props["translation-pending"] = "true";
        }
        return props;
    }

    private static void AddIf(Dictionary<string, string> props, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            props[key] = value.Trim();
        }
    }

    /// <summary>
    /// Extrae el tag de idioma del nombre del archivo. Convención WHO:
    /// <c>esES12LinguisticVariant.csv</c> → <c>es-ES</c>, <c>esMX28...</c> → <c>es-MX</c>,
    /// <c>frFR18...</c> → <c>fr-FR</c>.
    /// </summary>
    private static string ExtractLanguageTag(string fileName)
    {
        // Pattern: 2 letters (lang) + 2 letters (country) + digits + "LinguisticVariant.csv"
        if (fileName.Length < 4)
        {
            return "und";
        }
        var lang = fileName[..2].ToLowerInvariant();
        var country = fileName.Substring(2, 2).ToUpperInvariant();
        return $"{lang}-{country}";
    }

    private async Task<int> FlushBatchAsync(List<LoincCode> batch, CancellationToken cancellationToken)
    {
        await db.LoincCatalog.AddRangeAsync(batch, cancellationToken);
        var saved = await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return saved;
    }

    private sealed record MainRow(
        string LongCommonName,
        string? ShortName,
        string? Class,
        string? ClassType,
        string? Property,
        string? Scale,
        string? System_,
        string? TimeAspect,
        string? Method,
        string? IsPanel,
        string? OrderObs);

    private sealed record LinguisticVariantRow(
        string? LongCommonName,
        string? LinguisticVariantDisplayName);

    private sealed record DesignationEntry(string Language, string? Value);
}
