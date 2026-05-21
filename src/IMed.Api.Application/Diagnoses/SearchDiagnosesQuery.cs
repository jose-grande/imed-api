using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.Domain.Terminology;

namespace IMed.Api.Application.Diagnoses;

public sealed record SearchDiagnosesQuery(string Term, int Limit);

/// <summary>
/// Búsqueda typeahead de diagnósticos contra ICD-11 vía el port
/// <see cref="ITerminologyService"/>. El handler es agnóstico del provider
/// (Postgres hoy, HAPI Terminology o Snowstorm en el futuro) — sólo fija el
/// CodeSystem (ICD-11) y delega.
/// </summary>
public sealed class SearchDiagnosesHandler(ITerminologyService terminology)
{
    public async Task<IReadOnlyList<TerminologyConcept>> HandleAsync(
        SearchDiagnosesQuery query,
        CancellationToken cancellationToken = default)
    {
        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            // typeahead UX: sin esto el typeahead se dispararía con cada letra
            // y devolvería paginas de resultados al inicio del catálogo.
            return Array.Empty<TerminologyConcept>();
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var result = await terminology.SearchAsync(
            new TerminologySearchRequest
            {
                System = CodeSystems.Icd11,
                Term = term,
                Limit = limit,
                Language = "es",
                ActiveOnly = true,
            },
            cancellationToken);
        return result.Concepts;
    }
}
