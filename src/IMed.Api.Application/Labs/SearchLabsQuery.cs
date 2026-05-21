using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.Domain.Terminology;

namespace IMed.Api.Application.Labs;

public sealed record SearchLabsQuery(string Term, int Limit);

/// <summary>
/// Búsqueda typeahead de estudios de laboratorio contra LOINC vía el port
/// <see cref="ITerminologyService"/>. Mismo patrón que SearchDiagnosesHandler:
/// agnóstico del provider, sólo fija el CodeSystem (LOINC) y delega.
/// </summary>
public sealed class SearchLabsHandler(ITerminologyService terminology)
{
    public async Task<IReadOnlyList<TerminologyConcept>> HandleAsync(
        SearchLabsQuery query,
        CancellationToken cancellationToken = default)
    {
        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            return Array.Empty<TerminologyConcept>();
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var result = await terminology.SearchAsync(
            new TerminologySearchRequest
            {
                System = CodeSystems.Loinc,
                Term = term,
                Limit = limit,
                Language = "es",
                ActiveOnly = true,
            },
            cancellationToken);
        return result.Concepts;
    }
}
