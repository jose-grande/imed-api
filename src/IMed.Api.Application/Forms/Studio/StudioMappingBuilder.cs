using System.Text.Json.Nodes;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// Genera el <c>FhirMappingJson</c> de un documento del studio a partir de los bindings del
/// catálogo. El mapeo se guarda ya resuelto con cada versión publicada, así que un cambio
/// posterior en el catálogo no altera formularios publicados.
/// El documento debe haber pasado <see cref="StudioDocumentValidator"/>.
/// </summary>
public static class StudioMappingBuilder
{
    private const string CustomPrefix = "custom_";

    public static string Build(JsonNode document, FieldCatalog catalog, string extensionBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(catalog);

        var items = new JsonArray();

        foreach (var placed in StudioTree.Fields(StudioTree.Of(document)))
        {
            var def = catalog.Find(placed["field"]?.GetValue<string>() ?? string.Empty)
                ?? throw new InvalidOperationException("Documento no validado: campo fuera del catálogo.");
            var name = def.Custom ? placed["id"]!.GetValue<string>() : def.Key;

            var item = new JsonObject { ["linkId"] = name };
            foreach (var (key, value) in def.Binding)
            {
                item[key] = value?.DeepClone();
            }
            if (def.Custom)
            {
                var slug = name[CustomPrefix.Length..].Replace('_', '-');
                item["extensionUrl"] = $"{extensionBaseUrl.TrimEnd('/')}/{slug}";
            }
            items.Add(item);
        }

        return new JsonObject { ["version"] = "1.0", ["items"] = items }.ToJsonString();
    }
}
