namespace IMed.Api.Domain.Authorization;

/// <summary>Verbo FHIR sobre el que se decide, ya normalizado desde el método HTTP y la ruta.</summary>
public enum FhirVerb
{
    /// <summary>Lectura por id: <c>GET /Patient/123</c>.</summary>
    Read = 0,

    /// <summary>Búsqueda: <c>GET /Patient?name=...</c>.</summary>
    Search = 1,

    Create = 2,
    Update = 3,
    Delete = 4,

    /// <summary>Operación <c>$algo</c>. Se decide aparte porque casi ninguna debe estar abierta.</summary>
    Operation = 5,
}

/// <summary>
/// Regla de acceso a un tipo de recurso FHIR (ADR-007).
///
/// <para>La lista blanca del proxy es <b>dato</b> y no un <c>switch</c> en C#: agregar un tipo de
/// recurso es una fila, no un despliegue. Se modeló recién en SEC-3 y no en SEC-2 a propósito —
/// su forma la define el consumidor, y una tabla diseñada antes de saber cómo se consulta es una
/// tabla que hay que rehacer.</para>
///
/// <para><b>Varias reglas por (recurso, verbo) se suman.</b> Basta que el usuario tenga el permiso
/// de una para pasar. Así una lectura de <c>Patient</c> puede habilitarse tanto a quien ve
/// cualquier expediente como a quien solo busca por identificación, sin duplicar la tabla.</para>
/// </summary>
public sealed class FhirAccessRule
{
    public Guid Id { get; private set; }

    /// <summary>Nulo = regla global del producto; con valor = variante de esa organización.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Tipo de recurso FHIR, tal como aparece en la ruta: <c>Patient</c>, <c>Encounter</c>.</summary>
    public string ResourceType { get; private set; } = string.Empty;

    public FhirVerb Verb { get; private set; }

    /// <summary>Permiso que habilita esta combinación.</summary>
    public string PermissionCode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    private FhirAccessRule() { }

    public static FhirAccessRule Create(
        string resourceType,
        FhirVerb verb,
        string permissionCode,
        Guid? organizationId = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new ArgumentException("El tipo de recurso es obligatorio.", nameof(resourceType));
        }
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            throw new ArgumentException("El permiso es obligatorio.", nameof(permissionCode));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FhirAccessRule
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            ResourceType = resourceType.Trim(),
            Verb = verb,
            PermissionCode = permissionCode.Trim().ToLowerInvariant(),
            CreatedAt = now,
        };
    }
}
