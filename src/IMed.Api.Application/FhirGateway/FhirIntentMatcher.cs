using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>Resultado de hacer encajar un request contra el catálogo de intents.</summary>
/// <param name="Intent">El intent que encajó, o null.</param>
/// <param name="Reason">
/// Por qué no encajó. Se elige el motivo del candidato más cercano —mismo recurso y verbo— porque
/// en modo sombra este texto es el que dice qué hay que agregar al catálogo. "No hay intent para
/// Search sobre Encounter" manda a escribir uno; "el parámetro 'class' no está declarado en
/// 'patient.encounters'" manda a corregir el que ya existe, que casi siempre es lo correcto.
/// </param>
public sealed record FhirIntentMatch(FhirIntent? Intent, string? Reason)
{
    public bool Matched => Intent is not null;

    public static FhirIntentMatch Hit(FhirIntent intent) => new(intent, null);
    public static FhirIntentMatch Miss(string reason) => new(null, reason);
}

/// <summary>
/// Hace encajar un request FHIR contra las formas declaradas (SEC-4).
///
/// <para>Función pura: todo lo que necesita saber viene del request y del token ya resuelto. No
/// consulta HAPI, y esa es una restricción de diseño y no una omisión — resolver de quién es cada
/// recurso costaría un viaje a HAPI por lectura, en el camino caliente.</para>
///
/// <para><b>Gana el primero que encaja</b>, así que el orden del catálogo es parte de la
/// especificación. Lo específico antes que lo general.</para>
/// </summary>
public static class FhirIntentMatcher
{
    /// <param name="callerPractitionerId">
    /// Id FHIR del <c>Practitioner</c> del llamante, o null si no tiene uno asignado. Con null, los
    /// intents que exigen "tu propio profesional" no encajan — y caen al general, que pide lectura
    /// amplia. Es el lado correcto para equivocarse.
    /// </param>
    public static FhirIntentMatch Match(FhirRequestDescriptor request, string? callerPractitionerId)
    {
        string? nearest = null;

        foreach (var intent in FhirIntentCatalog.All)
        {
            if (!string.Equals(intent.ResourceType, request.ResourceType, StringComparison.Ordinal)
                || intent.Verb != request.Verb)
            {
                continue;
            }

            var complaint = Fits(intent, request, callerPractitionerId);
            if (complaint is null)
            {
                return FhirIntentMatch.Hit(intent);
            }

            nearest ??= complaint;
        }

        return FhirIntentMatch.Miss(nearest
            ?? $"No hay ninguna forma declarada para {request.Verb} sobre '{request.ResourceType}'.");
    }

    /// <summary>Devuelve null si encaja, o el motivo por el que no.</summary>
    private static string? Fits(FhirIntent intent, FhirRequestDescriptor request, string? callerPractitionerId)
    {
        // 1. Valores acotados. Va primero porque es la restricción más fuerte: distingue leer
        //    signos vitales de leer resultados de laboratorio con la misma forma de consulta.
        if (intent.RequiredValues is { Count: > 0 })
        {
            foreach (var (parameter, accepted) in intent.RequiredValues)
            {
                if (!TryGet(request, parameter, out var values))
                {
                    return $"'{intent.Name}' exige el parámetro '{parameter}'.";
                }

                // Todos los valores tienen que ser aceptados: alcanzaría con repetir el parámetro
                // para colar uno prohibido al lado de uno válido.
                foreach (var value in values)
                {
                    if (!accepted.Contains(value))
                    {
                        return $"'{intent.Name}' no acepta '{parameter}={value}'.";
                    }
                }
            }
        }

        // 2. Al menos un filtro que acote. Sin esto la consulta es una enumeración.
        if (intent.RequiredAnyOf is { Count: > 0 }
            && !intent.RequiredAnyOf.Any(p => TryGet(request, p, out _)))
        {
            return $"'{intent.Name}' exige al menos uno de: {string.Join(", ", intent.RequiredAnyOf)}.";
        }

        // 3. Nada sin declarar. Es la propiedad que el acotado de SEC-3 no daba: allí bastaba con
        //    no usar `_include` para que cualquier otro filtro viajara.
        foreach (var key in request.Query.Keys)
        {
            var root = FhirIntent.Root(key);
            if (FhirIntent.UndeclaredButAllowed.Contains(root)
                || intent.RequiredAnyOf?.Contains(root) == true
                || intent.Allowed?.Contains(root) == true
                || intent.RequiredValues?.ContainsKey(root) == true)
            {
                continue;
            }

            return $"'{intent.Name}' no declara el parámetro '{key}'.";
        }

        // 4. El filtro tiene que nombrarte a vos.
        if (intent.SelfPractitionerParam is { } selfParam)
        {
            if (!TryGet(request, selfParam, out var actors))
            {
                return $"'{intent.Name}' exige el parámetro '{selfParam}'.";
            }
            if (string.IsNullOrWhiteSpace(callerPractitionerId))
            {
                return $"'{intent.Name}' es para el profesional dueño de la agenda, y el usuario no tiene Practitioner asignado.";
            }
            foreach (var actor in actors)
            {
                if (!IsCaller(actor, callerPractitionerId))
                {
                    return $"'{intent.Name}' solo cubre tu propia agenda, y '{selfParam}' apunta a otro profesional.";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Busca un parámetro ignorando su modificador: <c>name:contains</c> cuenta como <c>name</c>.
    /// </summary>
    private static bool TryGet(FhirRequestDescriptor request, string parameter, out IReadOnlyList<string> values)
    {
        foreach (var (key, v) in request.Query)
        {
            if (string.Equals(FhirIntent.Root(key), parameter, StringComparison.OrdinalIgnoreCase))
            {
                values = v;
                return true;
            }
        }

        values = [];
        return false;
    }

    /// <summary>
    /// Si la referencia apunta al Practitioner del llamante. Se aceptan las dos formas con que FHIR
    /// las escribe: <c>Practitioner/1902</c> y <c>1902</c> pelado.
    /// </summary>
    private static bool IsCaller(string reference, string practitionerId)
    {
        var id = reference.Contains('/', StringComparison.Ordinal)
            ? reference[(reference.LastIndexOf('/') + 1)..]
            : reference;

        return string.Equals(id, practitionerId, StringComparison.Ordinal);
    }
}
