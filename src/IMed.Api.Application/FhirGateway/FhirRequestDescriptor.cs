using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>
/// Un request FHIR reducido a lo que la política necesita decidir: qué recurso, qué verbo y con
/// qué parámetros de búsqueda. Se construye igual desde un request HTTP que desde una entrada de
/// un Bundle de transacción, para que ambos caminos se evalúen con las mismas reglas.
/// </summary>
public sealed record FhirRequestDescriptor(
    string ResourceType,
    FhirVerb Verb,
    string? ResourceId,
    string? Operation,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Query)
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoQuery =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bundle de transacción: no es un recurso más, se abre y se evalúa por dentro.</summary>
    public bool IsBundle => string.Equals(ResourceType, "Bundle", StringComparison.Ordinal);
}

/// <summary>Resultado de evaluar un request contra la política.</summary>
public sealed record FhirAccessDecision(bool Allowed, string? Reason, string? DeniedResource = null)
{
    public static FhirAccessDecision Allow() => new(true, null);

    public static FhirAccessDecision Deny(string reason, string? resource = null) =>
        new(false, reason, resource);
}
