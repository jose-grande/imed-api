using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Terminology;

namespace IMed.Api.Application.Allergies;

public sealed record SearchAllergiesQuery(string Term, int Limit, string? Category);

/// <summary>DTO del endpoint <c>GET /api/allergies/search</c>.</summary>
public sealed record AllergyResultDto(string Code, string DisplayEs, string Category, string System);

/// <summary>
/// Búsqueda typeahead del catálogo de alergenos (Épica J1). El system es
/// siempre SNOMED CT — el slice local guarda solo los códigos reusables; el
/// frontend persiste el FHIR <c>AllergyIntolerance.code.coding</c> con ese
/// system.
/// </summary>
public sealed class SearchAllergiesHandler(IAllergyCatalogRepository repo)
{
    public async Task<IReadOnlyList<AllergyResultDto>> HandleAsync(
        SearchAllergiesQuery query,
        CancellationToken cancellationToken = default)
    {
        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            return Array.Empty<AllergyResultDto>();
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var results = await repo.SearchAsync(term, limit, query.Category, cancellationToken);
        return results
            .Select(a => new AllergyResultDto(a.Code, a.DisplayEs, a.Category, CodeSystems.SnomedCt))
            .ToList();
    }
}
