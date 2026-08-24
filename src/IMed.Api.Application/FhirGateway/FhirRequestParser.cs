using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.FhirGateway;

/// <summary>
/// Traduce una ruta FHIR a un <see cref="FhirRequestDescriptor"/>.
///
/// <para>Se usa el mismo parser para el request HTTP y para cada entrada de un Bundle: si fueran
/// dos, bastaría una diferencia sutil entre ambos para que algo prohibido por una vía pasara por
/// la otra — que es precisamente el agujero que el Bundle abre.</para>
/// </summary>
public static class FhirRequestParser
{
    public static FhirRequestDescriptor? Parse(string method, string url)
    {
        // Una ruta vacía con POST es el Bundle de transacción a la raíz — la forma en que el
        // cliente cierra un encuentro. Con cualquier otro método no describe nada.
        if (string.IsNullOrWhiteSpace(url))
        {
            return IsPost(method)
                ? new FhirRequestDescriptor("Bundle", FhirVerb.Create, null, null, FhirRequestDescriptor.NoQuery)
                : null;
        }

        var (pathPart, queryPart) = Split(url.TrimStart('/'));
        var segments = pathPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            // POST a la raíz = Bundle de transacción. Es la forma en que el cliente cierra un
            // encuentro, y la única razón por la que el evaluador tiene que abrir cuerpos.
            return IsPost(method)
                ? new FhirRequestDescriptor("Bundle", FhirVerb.Create, null, null, FhirRequestDescriptor.NoQuery)
                : null;
        }

        var resourceType = segments[0];
        if (resourceType.StartsWith('$'))
        {
            // Operación a nivel de servidor.
            return new FhirRequestDescriptor("*", FhirVerb.Operation, null, resourceType, ParseQuery(queryPart));
        }

        // `_history` a nivel de tipo o de instancia: se trata como operación y cae en la lista
        // blanca, que no la incluye. Un volcado de historial es una exportación del expediente.
        var operation = segments.FirstOrDefault(s => s.StartsWith('$'));
        var hasHistory = segments.Any(s => string.Equals(s, "_history", StringComparison.Ordinal));

        if (operation is not null || hasHistory)
        {
            return new FhirRequestDescriptor(
                resourceType, FhirVerb.Operation, null, operation ?? "_history", ParseQuery(queryPart));
        }

        var id = segments.Length > 1 ? segments[1] : null;

        var verb = method.ToUpperInvariant() switch
        {
            "GET" or "HEAD" => id is null ? FhirVerb.Search : FhirVerb.Read,
            "POST" => FhirVerb.Create,
            "PUT" or "PATCH" => FhirVerb.Update,
            "DELETE" => FhirVerb.Delete,
            _ => FhirVerb.Operation,
        };

        // Un POST con querystring sobre un tipo es la búsqueda por formulario (`_search`), no una
        // creación: tratarlo como creación abriría la búsqueda a quien solo puede crear.
        if (verb == FhirVerb.Create
            && segments.Length > 1
            && string.Equals(segments[1], "_search", StringComparison.Ordinal))
        {
            verb = FhirVerb.Search;
            id = null;
        }

        return new FhirRequestDescriptor(resourceType, verb, id, null, ParseQuery(queryPart));
    }

    private static bool IsPost(string method) =>
        string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

    private static (string Path, string Query) Split(string url)
    {
        var idx = url.IndexOf('?', StringComparison.Ordinal);
        return idx < 0 ? (url, string.Empty) : (url[..idx], url[(idx + 1)..]);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return FhirRequestDescriptor.NoQuery;
        }

        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Uri.UnescapeDataString(pair[(eq + 1)..]);
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }
            list.Add(value);
        }

        return result.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<string>)kv.Value,
            StringComparer.OrdinalIgnoreCase);
    }
}
