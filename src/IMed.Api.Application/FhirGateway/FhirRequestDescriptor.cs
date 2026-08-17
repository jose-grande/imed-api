using IMed.Api.Application.Abstractions;
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

/// <summary>
/// Quién está pidiendo, ya resuelto (SEC-4).
///
/// <para>Lo agrupa en un solo objeto porque el evaluador dejó de necesitar solo permisos: para
/// decidir si <c>actor=Practitioner/1902</c> te nombra a vos hace falta saber cuál es tu
/// <c>Practitioner</c>. Es lo único que se puede afirmar sobre pertenencia sin salir del proceso.</para>
/// </summary>
public sealed record FhirCallerContext(
    Guid OrganizationId,
    Guid? UserId,
    EffectivePermissions Permissions,
    string? PractitionerFhirId)
{
    public bool Has(string permissionCode) => Permissions.Has(permissionCode);
}

/// <summary>
/// Qué puerta negó. Existe para que SEC-3 y SEC-4 puedan estar en modos distintos.
///
/// <para>Sin esta distinción, poner la clasificación por forma en sombra habría obligado a bajar
/// también el proxy entero, que ya está en <c>Enforce</c> y verificado con uso real. Se retrocede
/// en lo nuevo sin retroceder en lo ganado.</para>
/// </summary>
public enum FhirDecisionStage
{
    /// <summary>Reglas de SEC-3: invariantes, acotado de búsqueda y lista blanca.</summary>
    Base = 0,

    /// <summary>Clasificación por forma de SEC-4.</summary>
    Intent = 1,
}

/// <summary>Resultado de evaluar un request contra la política.</summary>
/// <param name="Intent">Nombre de la forma reconocida. Es lo que se audita.</param>
/// <param name="Projection">Valor de <c>_elements</c> a forzar, o null para no recortar.</param>
/// <param name="BundleEntries">
/// Las entradas de un Bundle, ya descritas. Sin esto la bitácora registra <c>Create:Bundle</c> y
/// nada más, y la pregunta "¿quién escribió este diagnóstico?" se contesta con "alguien mandó un
/// Bundle".
/// </param>
public sealed record FhirAccessDecision(
    bool Allowed,
    string? Reason,
    string? DeniedResource = null,
    FhirDecisionStage Stage = FhirDecisionStage.Base,
    string? Intent = null,
    string? Projection = null,
    IReadOnlyList<string>? BundleEntries = null)
{
    public static FhirAccessDecision Allow() => new(true, null);

    public static FhirAccessDecision Allow(string intent, string? projection = null) =>
        new(true, null, Intent: intent, Projection: projection);

    public static FhirAccessDecision Deny(string reason, string? resource = null) =>
        new(false, reason, resource);

    /// <summary>Negada por la clasificación por forma. Se marca aparte para poder dejarla en sombra.</summary>
    public static FhirAccessDecision DenyShape(string reason, string? resource = null, string? intent = null) =>
        new(false, reason, resource, FhirDecisionStage.Intent, intent);

    public FhirAccessDecision With(IReadOnlyList<string> entries) => this with { BundleEntries = entries };
}
