using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.Abstractions;

/// <summary>Reglas de acceso FHIR aplicables a una organización (ADR-007, SEC-3).</summary>
public interface IFhirAccessRuleRepository
{
    /// <summary>
    /// Permisos que habilitan (recurso, verbo) en esta organización: las reglas globales más las
    /// suyas. Basta tener uno de ellos.
    /// </summary>
    Task<IReadOnlyList<string>> ListForAsync(
        Guid organizationId, string resourceType, FhirVerb verb, CancellationToken ct = default);
}
