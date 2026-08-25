using IMed.Api.Domain.Billing;

namespace IMed.Api.Application.Abstractions;

/// <summary>Partes: el otro lado de la transacción (FAC-1).</summary>
public interface IPartyRepository
{
    Task<Party?> GetAsync(Guid organizationId, Guid partyId, CancellationToken ct = default);

    /// <summary>La parte de un paciente del expediente, si ya existe.</summary>
    Task<Party?> FindByPatientAsync(
        Guid organizationId, string patientFhirId, CancellationToken ct = default);

    Task<IReadOnlyList<Party>> SearchAsync(
        Guid organizationId, string? term, int limit, CancellationToken ct = default);

    /// <summary>Nombres por id, en un solo viaje. Para listados que muestran a quién se le cobra.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> partyIds, CancellationToken ct = default);

    Task AddAsync(Party party, CancellationToken ct = default);
    Task UpdateAsync(Party party, CancellationToken ct = default);
}
