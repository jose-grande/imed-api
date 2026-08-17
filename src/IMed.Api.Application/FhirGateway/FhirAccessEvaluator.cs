using System.Globalization;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>
/// Decide si un request FHIR puede pasar al servidor (ADR-007, SEC-3 y SEC-4). Es el corazón del proxy.
///
/// <para><b>Default deny.</b> Lo que no está declarado en la lista blanca no pasa. Un tipo de
/// recurso nuevo rompe una pantalla de forma ruidosa y se arregla con una fila — que es el lado
/// correcto para equivocarse en salud.</para>
///
/// <para><b>Esto es lo que el interceptor nativo de HAPI regalaba</b> en la alternativa que ADR-007
/// descartó: la evaluación de Bundles entrada por entrada y el acotado de búsquedas. Al escribirlo
/// nosotros, es también donde una equivocación se paga en silencio, así que cada regla de acá
/// tiene su prueba.</para>
///
/// <para><b>Dos preguntas, no una.</b> Las puertas 1 a 6 contestan "¿está habilitado este recurso
/// para este verbo?" y salen de SEC-3. La puerta 7 contesta "¿esta consulta, con estos parámetros,
/// es una de las formas que declaramos?" y sale de SEC-4. Se marcan con etapas distintas para que
/// la segunda pueda correr en sombra mientras la primera bloquea.</para>
/// </summary>
public sealed class FhirAccessEvaluator(IFhirAccessRuleRepository rules)
{
    /// <summary>Tope de entradas de un Bundle. Más que eso no es una transacción, es una carga masiva.</summary>
    private const int MaxBundleEntries = 200;

    /// <summary>
    /// Si la decisión depende del cuerpo del request.
    ///
    /// <para>El proxy la consulta para no materializar el cuerpo de todos los requests, y el
    /// recorrido del Bundle para no copiar el JSON de cada entrada. Vive acá, junto a las reglas
    /// que lo necesitan, para que agregar una regla que mire el cuerpo no deje al proxy
    /// entregando <c>null</c> desde otro archivo.</para>
    /// </summary>
    public static bool RequiresBody(FhirRequestDescriptor request) =>
        request.IsBundle
        || (IsWrite(request.Verb) && request.ResourceType == FhirAccessCatalog.ObservationResource);

    public Task<FhirAccessDecision> EvaluateAsync(
        FhirCallerContext caller,
        FhirRequestDescriptor request,
        string? body,
        CancellationToken ct = default) =>
        EvaluateAsync(caller, request, body, depth: 0, ct);

    private async Task<FhirAccessDecision> EvaluateAsync(
        FhirCallerContext caller,
        FhirRequestDescriptor request,
        string? body,
        int depth,
        CancellationToken ct)
    {
        // 1. Operaciones: casi ninguna debe estar abierta. $expunge borra sin dejar rastro y
        //    $reindex puede tumbar el servidor bajo carga.
        if (request.Verb == FhirVerb.Operation)
        {
            var op = request.Operation ?? string.Empty;
            if (!FhirAccessCatalog.AllowedOperations.Contains(op))
            {
                return FhirAccessDecision.Deny($"La operación '{op}' no está habilitada.", request.ResourceType);
            }

            // Las operaciones que sí están habilitadas son de lectura ($expand, $lookup), así que
            // se evalúan contra las reglas de lectura del recurso. Declararlas aparte duplicaría
            // la lista blanca y abriría la puerta a que una quedara sin permiso exigido.
            request = request with { Verb = FhirVerb.Read };
        }

        // 2. El borrado no se abre por el lado público en ningún caso. Un expediente clínico no
        //    se borra: se corrige, y eso deja rastro.
        if (request.Verb == FhirVerb.Delete)
        {
            return FhirAccessDecision.Deny("El borrado no está habilitado desde el cliente.", request.ResourceType);
        }

        // 3. Bundle de transacción: se abre y se evalúa cada entrada con estas mismas reglas.
        if (request.IsBundle)
        {
            // El anidamiento se corta por profundidad y no por "la entrada no trae cuerpo": ahora
            // sí lo trae, porque las reglas que miran la categoría lo necesitan.
            if (depth > 0)
            {
                return FhirAccessDecision.Deny("Un Bundle no puede contener otro Bundle.", "Bundle");
            }

            return await EvaluateBundleAsync(caller, body, depth, ct);
        }

        // 4. Recursos con circuito de negocio: lectura sí, escritura jamás por acá.
        if (IsWrite(request.Verb))
        {
            if (FhirAccessCatalog.WriteProtectedResources.Contains(request.ResourceType))
            {
                return FhirAccessDecision.Deny(
                    $"'{request.ResourceType}' solo se escribe por los endpoints de negocio, que validan estado y rol.",
                    request.ResourceType);
            }

            if (request.ResourceType == FhirAccessCatalog.ObservationResource)
            {
                var category = CheckObservationCategory(body);
                if (!category.Allowed)
                {
                    return category;
                }
            }
        }

        // 5. Acotado de la búsqueda antes de mirar permisos: una consulta mal formada se rechaza
        //    aunque el usuario tuviera derecho al recurso.
        if (request.Verb == FhirVerb.Search)
        {
            var narrowing = CheckSearchShape(request);
            if (!narrowing.Allowed)
            {
                return narrowing;
            }
        }

        // 6. Lista blanca: basta un permiso de cualquiera de las reglas que apliquen.
        var applicable = await rules.ListForAsync(caller.OrganizationId, request.ResourceType, request.Verb, ct);
        if (applicable.Count == 0)
        {
            return FhirAccessDecision.Deny(
                $"No hay regla que habilite {request.Verb} sobre '{request.ResourceType}'.", request.ResourceType);
        }
        if (!applicable.Any(caller.Has))
        {
            return FhirAccessDecision.Deny(
                $"Falta permiso para {request.Verb} sobre '{request.ResourceType}'.", request.ResourceType);
        }

        // 7. Forma de la consulta (SEC-4). La puerta 6 dice si el recurso está habilitado; esta
        //    dice si ESTA consulta lo está. Va después a propósito: si fuera antes, una forma sin
        //    declarar quedaría marcada como etapa Intent, y en sombra pasaría aunque la lista
        //    blanca —que sí bloquea— la hubiera negado.
        return CheckShape(caller, request);
    }

    /// <summary>
    /// Hace encajar el request con una forma declarada y exige su método.
    /// </summary>
    private static FhirAccessDecision CheckShape(FhirCallerContext caller, FhirRequestDescriptor request)
    {
        var match = FhirIntentMatcher.Match(request, caller.PractitionerFhirId);
        if (!match.Matched)
        {
            return FhirAccessDecision.DenyShape(match.Reason!, request.ResourceType);
        }

        var intent = match.Intent!;
        if (!FhirAccessMethods.Satisfies(caller.Has, intent.Method))
        {
            return FhirAccessDecision.DenyShape(
                $"'{intent.Name}' exige el método '{intent.Method}'.", request.ResourceType, intent.Name);
        }

        // La proyección se le aplica solo a quien no puede ver cualquier expediente: recortarle el
        // paciente a quien ya tiene lectura amplia no protege nada y le rompe la pantalla.
        var projection = intent.Projection is not null && !FhirAccessMethods.HasBroadRecordRead(caller.Has)
            ? intent.Projection
            : null;

        return FhirAccessDecision.Allow(intent.Name, projection);
    }

    private static bool IsWrite(FhirVerb verb) =>
        verb is FhirVerb.Create or FhirVerb.Update or FhirVerb.Delete;

    /// <summary>
    /// Deja escribir una <c>Observation</c> según su categoría.
    ///
    /// <para>Los signos vitales y la antropometría los captura el personal a mano y no atraviesan
    /// ningún circuito; el resultado de laboratorio sí, y por eso <c>laboratory</c> no se escribe
    /// nunca por acá. El motivo largo está en <see cref="FhirAccessCatalog.WriteProtectedObservationCategories"/>.</para>
    ///
    /// <para><b>Sin categoría no pasa.</b> El default-deny vale también para el cuerpo: una
    /// Observation sin declarar qué es podría ser un resultado, y eso alcanza para negarla.</para>
    /// </summary>
    private static FhirAccessDecision CheckObservationCategory(string? body)
    {
        const string resource = FhirAccessCatalog.ObservationResource;

        if (string.IsNullOrWhiteSpace(body))
        {
            return FhirAccessDecision.Deny(
                "No se pudo leer el cuerpo de la Observation para determinar su categoría.", resource);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return FhirAccessDecision.Deny("La Observation no es JSON válido.", resource);
        }

        using (document)
        {
            var declared = false;

            if (document.RootElement.TryGetProperty("category", out var categories)
                && categories.ValueKind == JsonValueKind.Array)
            {
                foreach (var category in categories.EnumerateArray())
                {
                    if (!category.TryGetProperty("coding", out var codings)
                        || codings.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var coding in codings.EnumerateArray())
                    {
                        var code = coding.TryGetProperty("code", out var codeEl) ? codeEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(code))
                        {
                            continue;
                        }

                        declared = true;
                        if (FhirAccessCatalog.WriteProtectedObservationCategories.Contains(code))
                        {
                            return FhirAccessDecision.Deny(
                                $"Una Observation de categoría '{code}' solo se escribe por el circuito de laboratorio, "
                                + "que valida estado, orden y liberación.",
                                resource);
                        }
                    }
                }
            }

            return declared
                ? FhirAccessDecision.Allow()
                : FhirAccessDecision.Deny(
                    "La Observation no declara categoría, y sin categoría no se puede descartar que sea un resultado.",
                    resource);
        }
    }

    /// <summary>
    /// Acotado de búsquedas. Es por donde se filtra la información en sistemas de salud: una
    /// búsqueda de <c>Encounter</c> con un <c>_include</c> arbitrario devuelve los pacientes que
    /// el usuario no habría podido pedir directamente.
    /// </summary>
    private static FhirAccessDecision CheckSearchShape(FhirRequestDescriptor request)
    {
        foreach (var (key, values) in request.Query)
        {
            if (string.Equals(key, "_count", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var raw in values)
                {
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
                        || count <= 0
                        || count > FhirAccessCatalog.MaxCount)
                    {
                        return FhirAccessDecision.Deny(
                            $"_count debe estar entre 1 y {FhirAccessCatalog.MaxCount}.", request.ResourceType);
                    }
                }
                continue;
            }

            // `_has` viaja con el encadenamiento pegado a la clave —`_has:Observation:subject:code`—
            // así que comparar por igualdad exacta lo dejaría pasar. Se compara por prefijo, que es
            // como FHIR lo define. Lo mismo vale para `_include:iterate`.
            var root = key.Split(':', 2)[0];
            if (!FhirAccessCatalog.ScopeWideningParameters.Contains(root))
            {
                continue;
            }

            // `_has` y `_query` ensanchan de formas que no se pueden declarar caso por caso.
            if (!string.Equals(root, "_include", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(root, "_revinclude", StringComparison.OrdinalIgnoreCase))
            {
                return FhirAccessDecision.Deny($"El parámetro '{key}' no está habilitado.", request.ResourceType);
            }

            var allowed = FhirAccessCatalog.AllowedIncludes.TryGetValue(request.ResourceType, out var set)
                ? set
                : null;
            foreach (var value in values)
            {
                // `Encounter:patient:Patient` y `Encounter:patient` son el mismo include; se
                // compara por los dos primeros segmentos, que es lo que define el alcance.
                var parts = value.Split(':');
                var normalized = parts.Length >= 2 ? $"{parts[0]}:{parts[1]}" : value;
                if (allowed is null || !allowed.Contains(normalized))
                {
                    return FhirAccessDecision.Deny(
                        $"El include '{value}' no está declarado para '{request.ResourceType}'.", request.ResourceType);
                }
            }
        }

        return FhirAccessDecision.Allow();
    }

    /// <summary>
    /// Abre el Bundle y evalúa cada entrada. Es la pieza más delicada del proxy: un Bundle puede
    /// llevar adentro cualquier operación sobre cualquier recurso, así que mirar solo el verbo y
    /// la ruta de afuera dejaría pasar exactamente lo que el resto de las reglas prohíbe.
    ///
    /// <para>Además arma la lista de entradas para la bitácora. Sin ella el registro dice
    /// <c>Create:Bundle</c> y nada más, y la pregunta "¿quién escribió este diagnóstico?" se
    /// contesta con "alguien mandó un Bundle".</para>
    /// </summary>
    private async Task<FhirAccessDecision> EvaluateBundleAsync(
        FhirCallerContext caller, string? body, int depth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return FhirAccessDecision.Deny("Bundle vacío.", "Bundle");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Un Bundle que no se puede leer no se puede evaluar, y lo que no se puede evaluar
            // no pasa. Dejarlo seguir sería confiar en que el servidor de atrás lo rechace.
            return FhirAccessDecision.Deny("El Bundle no es JSON válido.", "Bundle");
        }

        using var _ = document;
        var root = document.RootElement;

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return FhirAccessDecision.Deny("El Bundle no trae entradas.", "Bundle");
        }
        if (entries.GetArrayLength() > MaxBundleEntries)
        {
            return FhirAccessDecision.Deny(
                $"El Bundle supera las {MaxBundleEntries} entradas permitidas.", "Bundle");
        }

        var trace = new List<string>(entries.GetArrayLength());

        foreach (var entry in entries.EnumerateArray())
        {
            var descriptor = DescribeEntry(entry);
            if (descriptor is null)
            {
                return FhirAccessDecision
                    .Deny("Una entrada del Bundle no declara su request.", "Bundle")
                    .With(trace);
            }

            // El recurso de la entrada es el cuerpo de ese request. Solo se copia cuando alguna
            // regla lo va a mirar: en un Bundle de 200 entradas, extraer el JSON de todas para
            // no usarlo sería trabajo puro.
            string? entryBody = null;
            if (RequiresBody(descriptor)
                && entry.TryGetProperty("resource", out var resource)
                && resource.ValueKind == JsonValueKind.Object)
            {
                entryBody = resource.GetRawText();
            }

            var decision = await EvaluateAsync(caller, descriptor, entryBody, depth + 1, ct);
            trace.Add(Describe(descriptor, decision));

            if (!decision.Allowed)
            {
                // Se rechaza el Bundle **entero**. Aplicar la parte autorizada dejaría la
                // transacción a medias, que es peor que no haberla intentado.
                return new FhirAccessDecision(
                    false,
                    $"Entrada rechazada del Bundle: {decision.Reason}",
                    decision.DeniedResource,
                    decision.Stage,
                    decision.Intent).With(trace);
            }
        }

        // La forma del Bundle mismo. Va al final para que la traza de entradas quede completa
        // aunque el propio Bundle no encaje.
        var shape = CheckShape(caller, new FhirRequestDescriptor(
            "Bundle", FhirVerb.Create, null, null, FhirRequestDescriptor.NoQuery));

        return shape.With(trace);
    }

    /// <summary>Etiqueta de una entrada para la bitácora: qué se hizo y con qué forma.</summary>
    private static string Describe(FhirRequestDescriptor entry, FhirAccessDecision decision)
    {
        var target = entry.ResourceId is null
            ? entry.ResourceType
            : $"{entry.ResourceType}/{entry.ResourceId}";

        return decision.Intent is null
            ? $"{entry.Verb}:{target}"
            : $"{entry.Verb}:{target} ({decision.Intent})";
    }

    /// <summary>Traduce <c>entry.request</c> (método + url) al mismo descriptor que un request HTTP.</summary>
    private static FhirRequestDescriptor? DescribeEntry(JsonElement entry)
    {
        if (!entry.TryGetProperty("request", out var req)
            || !req.TryGetProperty("method", out var methodEl)
            || !req.TryGetProperty("url", out var urlEl))
        {
            return null;
        }

        var method = methodEl.GetString() ?? string.Empty;
        var url = urlEl.GetString() ?? string.Empty;
        return FhirRequestParser.Parse(method, url);
    }
}
