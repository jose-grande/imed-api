using System.Diagnostics;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Terminology;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace IMed.Api.Infrastructure.Terminology.Repositories;

/// <summary>
/// Reader Postgres para el CodeSystem ICD-11. Implementa
/// <see cref="INamedTerminologyCatalogReader"/> y se descubre automáticamente
/// vía <c>IEnumerable&lt;INamedTerminologyCatalogReader&gt;</c> en el adapter
/// <see cref="PostgresTerminologyService"/>.
/// </summary>
internal sealed class Icd11CatalogReader(AppDbContext db) : INamedTerminologyCatalogReader
{
    public string System => CodeSystems.Icd11;

    public async Task<TerminologySearchResult> SearchAsync(
        TerminologySearchRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var tsQuery = PostgresFullTextSearch.BuildTsQuery(request.Term);
        if (string.IsNullOrEmpty(tsQuery))
        {
            return new TerminologySearchResult
            {
                Concepts = Array.Empty<TerminologyConcept>(),
                Total = 0,
                ProviderName = PostgresTerminologyService.ProviderName,
                Elapsed = sw.Elapsed
            };
        }

        var query = db.Icd11Catalog.AsNoTracking();
        if (request.ActiveOnly)
        {
            query = query.Where(x => x.Active);
        }

        // Match: FTS sobre display (es+en) OR prefix-ILIKE sobre code. La columna
        // tsv NO indexa el code — un user que tipea "1A03" debe matchear el code
        // directo además de la búsqueda por display. El prefix-match usa el GIN
        // trigram sobre code, lo que permite también tolerar typos.
        var codePrefix = $"{request.Term.Trim()}%";
        var rows = await query
            .Where(x =>
                EF.Property<NpgsqlTsVector>(x, "SearchVector")
                    .Matches(EF.Functions.ToTsQuery("spanish", tsQuery))
                || EF.Functions.ILike(x.Code, codePrefix))
            .OrderByDescending(x => EF.Property<NpgsqlTsVector>(x, "SearchVector")
                .RankCoverDensity(EF.Functions.ToTsQuery("spanish", tsQuery)))
            .ThenBy(x => x.Code)
            .Take(request.Limit)
            .ToListAsync(cancellationToken);

        var concepts = rows.Select(MapToConcept).ToArray();
        return new TerminologySearchResult
        {
            Concepts = concepts,
            Total = concepts.Length,
            ProviderName = PostgresTerminologyService.ProviderName,
            Elapsed = sw.Elapsed
        };
    }

    public async Task<TerminologyConcept?> LookupAsync(
        string code,
        string? language,
        CancellationToken cancellationToken)
    {
        var normalized = code.Trim();
        var entity = await db.Icd11Catalog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, cancellationToken);
        return entity is null ? null : MapToConcept(entity);
    }

    public async Task<bool> ValidateCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim();
        return await db.Icd11Catalog
            .AsNoTracking()
            .AnyAsync(x => x.Code == normalized && x.Active, cancellationToken);
    }

    private static TerminologyConcept MapToConcept(Icd11Code entity)
    {
        var designations = entity.DisplayEn is null
            ? null
            : new TerminologyDesignation[]
            {
                new() { Language = "en", Value = entity.DisplayEn, Use = "synonym" }
            };

        return new TerminologyConcept
        {
            System = CodeSystems.Icd11,
            Code = entity.Code,
            Display = entity.DisplayEs,
            Designations = designations,
            Properties = entity.Properties.Count > 0 ? entity.Properties : null,
        };
    }
}
