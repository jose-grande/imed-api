using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Terminology;

namespace IMed.Api.Application.Medications;

public sealed record SearchMedicationsQuery(string Term, int Limit, string? Category);

/// <summary>DTO del endpoint <c>GET /api/medications/search</c>.</summary>
public sealed record MedicationResultDto(
    string AtcCode,
    string NameGeneric,
    string? NameCommercial,
    string? Presentation,
    string Category,
    string System);

/// <summary>
/// Búsqueda typeahead del catálogo de medicamentos (Épica K1). El system es
/// siempre ATC — el frontend lo usa al construir
/// <c>MedicationRequest.medication.coding</c>.
/// </summary>
public sealed class SearchMedicationsHandler(IMedicationCatalogRepository repo)
{
    public async Task<IReadOnlyList<MedicationResultDto>> HandleAsync(
        SearchMedicationsQuery query,
        CancellationToken cancellationToken = default)
    {
        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            return Array.Empty<MedicationResultDto>();
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var results = await repo.SearchAsync(term, limit, query.Category, cancellationToken);
        return results
            .Select(m => new MedicationResultDto(
                AtcCode: m.AtcCode,
                NameGeneric: m.NameGeneric,
                NameCommercial: m.NameCommercial,
                Presentation: m.Presentation,
                Category: m.Category,
                System: CodeSystems.Atc))
            .ToList();
    }
}
