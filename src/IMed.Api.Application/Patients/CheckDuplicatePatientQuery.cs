using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;

namespace IMed.Api.Application.Patients;

public sealed record CheckDuplicatePatientQuery(
    Guid OrganizationId,
    string IdentifierSystem,
    string IdentifierValue);

public sealed record CheckDuplicatePatientResult(
    bool HasMatches,
    IReadOnlyList<PatientMatch> Matches);

/// <summary>
/// Dedupe dentro de la partición del tenant (Épica H2). El frontend lo llama
/// on-blur en el campo identifier antes de submit; si <c>HasMatches=true</c>,
/// muestra un modal para que el admin decida (abrir paciente existente vs.
/// crear igual). Cross-tenant queda fuera de scope — la partition aísla.
/// </summary>
public sealed class CheckDuplicatePatientHandler(
    IOrganizationProfileRepository orgRepo,
    IHapiFhirClient hapi)
{
    public async Task<CheckDuplicatePatientResult> HandleAsync(
        CheckDuplicatePatientQuery query,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.IdentifierSystem)
            || string.IsNullOrWhiteSpace(query.IdentifierValue))
        {
            return new CheckDuplicatePatientResult(HasMatches: false, Matches: Array.Empty<PatientMatch>());
        }

        var org = await orgRepo.GetByIdAsync(query.OrganizationId, cancellationToken);
        if (org is null)
        {
            // Sin org no podemos buscar — devolvemos no-match (defensa; el
            // endpoint hubiera tenido que rechazar antes).
            return new CheckDuplicatePatientResult(HasMatches: false, Matches: Array.Empty<PatientMatch>());
        }

        var partitionName = $"org-{query.OrganizationId}";
        var matches = await hapi.SearchPatientsByIdentifierAsync(
            query.IdentifierSystem,
            query.IdentifierValue,
            partitionName,
            cancellationToken);

        return new CheckDuplicatePatientResult(HasMatches: matches.Count > 0, Matches: matches);
    }
}
