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
/// Reader Postgres para el CodeSystem LOINC. Mismo patrón que
/// <c>Icd11CatalogReader</c>; ver allí los detalles del FTS y mapping.
/// </summary>
internal sealed class LoincCatalogReader(AppDbContext db) : INamedTerminologyCatalogReader
{
    public string System => CodeSystems.Loinc;

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

        var query = db.LoincCatalog.AsNoTracking();
        if (request.ActiveOnly)
        {
            query = query.Where(x => x.Active);
        }

        // Match: FTS sobre display (es+en) OR prefix-ILIKE sobre code. Crítico
        // para LOINC porque los users a menudo escriben el code directo (ej. "718"
        // → 718-7). El code NO está en el tsv, hay que matchearlo por columna.
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
        var entity = await db.LoincCatalog
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Code == normalized, cancellationToken);
        return entity is null ? null : MapToConcept(entity);
    }

    public async Task<bool> ValidateCodeAsync(string code, CancellationToken cancellationToken)
    {
        var normalized = code.Trim();
        return await db.LoincCatalog
            .AsNoTracking()
            .AnyAsync(x => x.Code == normalized && x.Active, cancellationToken);
    }

    private static TerminologyConcept MapToConcept(LoincCode entity)
    {
        var designations = entity.DisplayEn is null
            ? null
            : new TerminologyDesignation[]
            {
                new() { Language = "en", Value = entity.DisplayEn, Use = "synonym" }
            };

        return new TerminologyConcept
        {
            System = CodeSystems.Loinc,
            Code = entity.Code,
            Display = entity.DisplayEs,
            Designations = designations,
            Properties = entity.Properties.Count > 0 ? entity.Properties : null,
        };
    }
}
