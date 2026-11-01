using System.Text.Json;
using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// Punto único por donde pasa un formulario al guardarse o publicarse. Si el <c>SchemaJson</c> es
/// un documento del studio, lo valida contra el catálogo y reemplaza el mapeo por el generado; si
/// no lo es (los formularios de encuentro), no toca nada.
/// </summary>
public sealed class StudioFormProcessor(IFieldCatalogProvider catalogs, FormStudioOptions options)
{
    public sealed record Result(string SchemaJson, string FhirMappingJson, string? DocumentTitle);

    /// <exception cref="ArgumentException">El documento no es válido o no hay catálogo para el formulario.</exception>
    public async Task<Result> PrepareAsync(
        string code,
        string schemaJson,
        string fhirMappingJson,
        CancellationToken cancellationToken = default)
    {
        var document = TryParseStudioDocument(schemaJson);
        if (document is null)
        {
            return new Result(schemaJson, fhirMappingJson, null);
        }

        var catalog = await catalogs.FindAsync(code, cancellationToken)
            ?? throw new ArgumentException($"No hay catálogo de campos para el formulario '{code}'.");

        var errors = StudioDocumentValidator.Validate(document, catalog);
        if (errors.Count > 0)
        {
            throw new ArgumentException("Documento de formulario inválido: " + string.Join(" ", errors));
        }

        var title = document["title"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;
        return new Result(
            schemaJson,
            StudioMappingBuilder.Build(document, catalog, options.ExtensionBaseUrl),
            title);
    }

    private static JsonObject? TryParseStudioDocument(string schemaJson)
    {
        try
        {
            var node = JsonNode.Parse(schemaJson);
            return node is JsonObject obj
                && obj["kind"] is JsonValue kind
                && kind.TryGetValue<string>(out var value)
                && (value == StudioDocumentValidator.Kind || value == StudioDocumentValidator.TreeKind)
                ? obj
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
