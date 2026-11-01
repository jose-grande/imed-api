using System.Text.Json.Nodes;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// El formulario como árbol (<c>kind: studio-v2</c>): la tarjeta lleva campos sueltos y cajas
/// (<c>container</c>, <c>fieldset</c>, <c>tabs</c>, <c>accordion</c>), y las cajas llevan más de ambos.
/// Un documento de secciones y filas (<c>studio-v1</c>) se lee como un árbol con <see cref="ToTree"/>.
/// </summary>
public static class StudioTree
{
    private const int RowGap = 6;

    /// <summary>Cada sección es una pestaña y cada fila un contenedor con una columna por campo. El documento debe ser válido.</summary>
    public static JsonObject ToTree(JsonObject document)
    {
        var tabs = new JsonArray();
        foreach (var section in document["sections"]?.AsArray() ?? [])
        {
            var sectionId = section!["id"]!.GetValue<string>();
            var rows = new JsonArray();
            var index = 0;
            foreach (var row in section["rows"]!.AsArray())
            {
                index++;
                var children = new JsonArray();
                foreach (var placed in row!.AsArray())
                {
                    // "type" first, as the client writes it.
                    var field = new JsonObject { ["type"] = "field" };
                    foreach (var (key, value) in (JsonObject)placed!)
                    {
                        field[key] = value?.DeepClone();
                    }
                    children.Add(field);
                }
                rows.Add(new JsonObject
                {
                    ["type"] = "container",
                    ["id"] = $"{sectionId}_row{index}",
                    ["layout"] = "grid",
                    ["columns"] = ((JsonArray)row).Count,
                    ["gap"] = RowGap,
                    ["children"] = children,
                });
            }
            tabs.Add(new JsonObject { ["id"] = sectionId, ["label"] = section["label"]?.DeepClone(), ["children"] = rows });
        }

        var tree = new JsonObject { ["kind"] = StudioDocumentValidator.TreeKind };
        if (document["basedOn"] is { } basedOn)
        {
            tree["basedOn"] = basedOn.DeepClone();
        }
        if (document["title"] is { } title)
        {
            tree["title"] = title.DeepClone();
        }
        tree["children"] = new JsonArray(new JsonObject { ["type"] = "tabs", ["id"] = "form_tabs", ["tabs"] = tabs });
        return tree;
    }

    /// <summary>El documento como árbol, venga como árbol o como secciones y filas.</summary>
    public static JsonObject Of(JsonNode document) =>
        document is JsonObject obj && StudioDocumentValidator.AsString(obj["kind"]) == StudioDocumentValidator.TreeKind
            ? obj
            : ToTree((JsonObject)document);

    /// <summary>Los campos colocados en el árbol, en el orden en que se leen.</summary>
    public static IEnumerable<JsonObject> Fields(JsonObject tree) => FieldsIn(tree["children"]);

    private static IEnumerable<JsonObject> FieldsInItems(JsonNode? items) =>
        (items?.AsArray() ?? []).SelectMany(item => FieldsIn(item?["children"]));

    private static IEnumerable<JsonObject> FieldsIn(JsonNode? nodes)
    {
        foreach (var node in nodes?.AsArray() ?? [])
        {
            if (node is not JsonObject obj)
            {
                continue;
            }
            switch (StudioDocumentValidator.AsString(obj["type"]))
            {
                case "field":
                    yield return obj;
                    break;
                case "container":
                case "fieldset":
                    foreach (var field in FieldsIn(obj["children"]))
                    {
                        yield return field;
                    }
                    break;
                case "tabs":
                    foreach (var field in FieldsInItems(obj["tabs"]))
                    {
                        yield return field;
                    }
                    break;
                case "accordion":
                    foreach (var field in FieldsInItems(obj["sections"]))
                    {
                        yield return field;
                    }
                    break;
            }
        }
    }
}
