using System.Globalization;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>
/// Decide si un request FHIR puede pasar al servidor (ADR-007, SEC-3). Es el corazón del proxy.
///
/// <para><b>Default deny.</b> Lo que no está declarado en la lista blanca no pasa. Un tipo de
/// recurso nuevo rompe una pantalla de forma ruidosa y se arregla con una fila — que es el lado
/// correcto para equivocarse en salud.</para>
///
/// <para><b>Esto es lo que el interceptor nativo de HAPI regalaba</b> en la alternativa que ADR-007
/// descartó: la evaluación de Bundles entrada por entrada y el acotado de búsquedas. Al escribirlo
/// nosotros, es también donde una equivocación se paga en silencio, así que cada regla de acá
/// tiene su prueba.</para>
/// </summary>
public sealed class FhirAccessEvaluator(IFhirAccessRuleRepository rules)
{
    /// <summary>Profundidad máxima de un Bundle. Un Bundle dentro de otro no tiene uso legítimo acá.</summary>
    private const int MaxBundleEntries = 200;

    public async Task<FhirAccessDecision> EvaluateAsync(
        Guid organizationId,
        EffectivePermissions permissions,
        FhirRequestDescriptor request,
        string? body,
        CancellationToken ct = default)
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
            return await EvaluateBundleAsync(organizationId, permissions, body, ct);
        }

        // 4. Recursos con circuito de negocio: lectura sí, escritura jamás por acá.
        if (IsWrite(request.Verb)
            && FhirAccessCatalog.WriteProtectedResources.Contains(request.ResourceType))
        {
            return FhirAccessDecision.Deny(
                $"'{request.ResourceType}' solo se escribe por los endpoints de negocio, que validan estado y rol.",
                request.ResourceType);
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
        var applicable = await rules.ListForAsync(organizationId, request.ResourceType, request.Verb, ct);
        if (applicable.Count == 0)
        {
            return FhirAccessDecision.Deny(
                $"No hay regla que habilite {request.Verb} sobre '{request.ResourceType}'.", request.ResourceType);
        }
        if (!applicable.Any(permissions.Has))
        {
            return FhirAccessDecision.Deny(
                $"Falta permiso para {request.Verb} sobre '{request.ResourceType}'.", request.ResourceType);
        }

        return FhirAccessDecision.Allow();
    }

    private static bool IsWrite(FhirVerb verb) =>
        verb is FhirVerb.Create or FhirVerb.Update or FhirVerb.Delete;

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
    /// </summary>
    private async Task<FhirAccessDecision> EvaluateBundleAsync(
        Guid organizationId, EffectivePermissions permissions, string? body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return FhirAccessDecision.Deny("Bundle vacío.", "Bundle");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(body).RootElement;
        }
        catch (JsonException)
        {
            // Un Bundle que no se puede leer no se puede evaluar, y lo que no se puede evaluar
            // no pasa. Dejarlo seguir sería confiar en que el servidor de atrás lo rechace.
            return FhirAccessDecision.Deny("El Bundle no es JSON válido.", "Bundle");
        }

        if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return FhirAccessDecision.Deny("El Bundle no trae entradas.", "Bundle");
        }
        if (entries.GetArrayLength() > MaxBundleEntries)
        {
            return FhirAccessDecision.Deny(
                $"El Bundle supera las {MaxBundleEntries} entradas permitidas.", "Bundle");
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var descriptor = DescribeEntry(entry);
            if (descriptor is null)
            {
                return FhirAccessDecision.Deny("Una entrada del Bundle no declara su request.", "Bundle");
            }

            // Recursión de un solo nivel: un Bundle anidado se rechaza en la llamada de abajo,
            // que no tiene cuerpo propio que abrir.
            var decision = await EvaluateAsync(organizationId, permissions, descriptor, null, ct);
            if (!decision.Allowed)
            {
                // Se rechaza el Bundle **entero**. Aplicar la parte autorizada dejaría la
                // transacción a medias, que es peor que no haberla intentado.
                return FhirAccessDecision.Deny(
                    $"Entrada rechazada del Bundle: {decision.Reason}", decision.DeniedResource);
            }
        }

        return FhirAccessDecision.Allow();
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
