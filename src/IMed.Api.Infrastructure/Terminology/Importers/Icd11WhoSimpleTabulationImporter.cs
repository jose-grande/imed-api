using System.Globalization;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Terminology;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Terminology.Importers;

/// <summary>
/// Importer del catálogo ICD-11 desde el formato WHO Simple Tabulation (TSV).
/// Spec: <a href="https://icd.who.int/dev11/Downloads/Download">WHO ICD-11 Downloads</a>.
///
/// <para>Schema esperado (tab-separated, primera fila headers):
/// <c>Foundation URI \t Linearization URI \t Code \t BlockId \t TitleEN \t Title \t ClassKind \t DepthInKind \t IsResidual \t ChapterNo \t BrowserLink \t isLeaf \t Primary tabulation \t Grouping1..5</c></para>
///
/// <para>Filtros:</para>
/// <list type="bullet">
///   <item><c>Code</c> no vacío (descarta chapters y blocks que no tienen código).</item>
///   <item><c>ClassKind = "category"</c> (descarta chapter/block aunque traigan code raro).</item>
/// </list>
///
/// <para>Transformaciones:</para>
/// <list type="bullet">
///   <item>Strippea prefijo visual <c>"- "</c> repetido del título (representa profundidad en el browser WHO).</item>
///   <item>Deriva <c>parent-code</c> por convención de prefijos: <c>1A03.0 → 1A03</c>.</item>
///   <item>Almacena <c>chapter</c>, <c>is-leaf</c>, <c>foundation-uri</c> en Properties JSONB.</item>
/// </list>
/// </summary>
internal sealed partial class Icd11WhoSimpleTabulationImporter(
    AppDbContext db,
    ILogger<Icd11WhoSimpleTabulationImporter> logger) : ITerminologyCatalogImporter
{
    public string CodeSystem => CodeSystems.Icd11;

    public string SourceFormat => "who-simple-tabulation";

    private const int BatchSize = 1000;

    [GeneratedRegex(@"^(?:- )+", RegexOptions.Compiled)]
    private static partial Regex VisualPrefixRegex();

    public async Task<ImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = "\t",
            HasHeaderRecord = true,
            // El TSV de WHO tiene grouping4/5 vacíos en muchas filas y headers
            // con espacios — toleramos para no fallar el import entero.
            BadDataFound = null,
            MissingFieldFound = null,
        };

        await using var source = File.OpenRead(sourcePath);
        using var reader = new StreamReader(source);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        var inserted = 0;
        var skipped = 0;
        var errors = 0;
        var seenCodes = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<Icd11Code>(BatchSize);

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!TryBuildEntity(csv, seenCodes, out var entity, out var skipReason))
                {
                    skipped++;
                    if (skipReason is not null)
                    {
                        logger.LogTrace("Skip ICD-11 row: {Reason}", skipReason);
                    }
                    continue;
                }

                batch.Add(entity);
                if (batch.Count >= BatchSize)
                {
                    inserted += await FlushBatchAsync(batch, cancellationToken);
                    batch.Clear();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Error procesando fila ICD-11 (skipping).");
                errors++;
            }
        }

        if (batch.Count > 0)
        {
            inserted += await FlushBatchAsync(batch, cancellationToken);
        }

        return new ImportResult(inserted, skipped, errors);
    }

    private static bool TryBuildEntity(
        CsvReader csv,
        HashSet<string> seenCodes,
        out Icd11Code entity,
        out string? skipReason)
    {
        entity = null!;
        skipReason = null;

        var code = csv.GetField<string>("Code");
        if (string.IsNullOrWhiteSpace(code))
        {
            skipReason = "code empty (chapter/block sin code)";
            return false;
        }

        var classKind = csv.GetField<string>("ClassKind");
        if (!string.Equals(classKind, "category", StringComparison.OrdinalIgnoreCase))
        {
            skipReason = $"classKind={classKind ?? "(null)"} no es category";
            return false;
        }

        var trimmedCode = code.Trim();
        if (!seenCodes.Add(trimmedCode))
        {
            skipReason = $"code duplicado: {trimmedCode}";
            return false;
        }

        var titleEs = csv.GetField<string>("Title") ?? string.Empty;
        var displayEs = StripVisualPrefix(titleEs);
        if (string.IsNullOrWhiteSpace(displayEs))
        {
            skipReason = "Title (es) vacío";
            return false;
        }

        var titleEn = csv.GetField<string>("TitleEN") ?? string.Empty;
        var displayEn = StripVisualPrefix(titleEn);

        var props = BuildProperties(csv, trimmedCode);

        entity = Icd11Code.Create(
            code: trimmedCode,
            displayEs: displayEs,
            displayEn: string.IsNullOrWhiteSpace(displayEn) ? null : displayEn,
            properties: props,
            active: true);
        return true;
    }

    private static string StripVisualPrefix(string title)
    {
        return VisualPrefixRegex().Replace(title.Trim(), string.Empty).Trim();
    }

    private static Dictionary<string, string> BuildProperties(CsvReader csv, string code)
    {
        var props = new Dictionary<string, string>();

        var chapter = csv.GetField<string>("ChapterNo");
        if (!string.IsNullOrWhiteSpace(chapter))
        {
            props["chapter"] = chapter.Trim();
        }

        var isLeaf = csv.GetField<string>("isLeaf");
        if (!string.IsNullOrWhiteSpace(isLeaf))
        {
            // Normalizar a "true"/"false" lowercase (consumers ven valor estable).
            props["is-leaf"] = isLeaf.Trim().ToLowerInvariant();
        }

        var foundationUri = csv.GetField<string>("Foundation URI");
        if (!string.IsNullOrWhiteSpace(foundationUri))
        {
            props["foundation-uri"] = foundationUri.Trim();
        }

        var parent = DeriveParentCode(code);
        if (parent is not null)
        {
            props["parent-code"] = parent;
        }

        return props;
    }

    /// <summary>
    /// Deriva parent-code por convención de prefijos del code: <c>1A03.0 → 1A03</c>,
    /// <c>1A03.Z → 1A03</c>. Para codes sin punto (<c>1A03</c>) devuelve null —
    /// el "parent" sería el block o chapter, que no tiene code propio en MMS.
    /// </summary>
    private static string? DeriveParentCode(string code)
    {
        var dotIdx = code.LastIndexOf('.');
        return dotIdx > 0 ? code[..dotIdx] : null;
    }

    private async Task<int> FlushBatchAsync(List<Icd11Code> batch, CancellationToken cancellationToken)
    {
        // EF AddRange + SaveChanges. Para 17-20k rows totales rinde ~3s sin
        // necesidad de Npgsql COPY. ChangeTracker.Clear() libera memoria entre
        // batches; sin esto, el tracker acumularía las 17k entities en heap.
        await db.Icd11Catalog.AddRangeAsync(batch, cancellationToken);
        var saved = await db.SaveChangesAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return saved;
    }
}
