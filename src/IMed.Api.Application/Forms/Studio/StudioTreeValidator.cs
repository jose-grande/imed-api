using System.Text.Json.Nodes;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// Valida un documento en árbol (<c>kind: studio-v2</c>). Son las mismas reglas y mensajes que el
/// compilador del cliente.
/// </summary>
public static class StudioTreeValidator
{
    public const int MaxDepth = 4;
    public const int MaxNodes = 500;
    public const int MaxChildren = 50;
    public const int MaxItems = 20;
    public const int MaxColumns = 6;
    public const int MaxGap = 12;
    public const int MaxSpan = 12;
    public const int MaxFields = StudioDocumentValidator.MaxFields;

    private static readonly string[] Layouts = ["grid", "vertical"];

    private sealed class Check(FieldCatalog catalog)
    {
        public FieldCatalog Catalog { get; } = catalog;
        public HashSet<string> Used { get; } = [];
        public HashSet<string> Ids { get; } = [];
        public List<string> Errors { get; } = [];
        public int Nodes { get; set; }
        public int Fields { get; set; }
    }

    public static IReadOnlyList<string> Validate(JsonObject root, FieldCatalog catalog)
    {
        var check = new Check(catalog);
        StudioDocumentValidator.UnknownKeys(root, ["kind", "basedOn", "title", "children"], "the form", check.Errors);

        if (root["children"] is not JsonArray { Count: > 0 } children)
        {
            check.Errors.Add("The form needs at least one element.");
            return check.Errors;
        }

        CheckChildren(children, 1, "the form", check);

        if (check.Nodes > MaxNodes)
        {
            check.Errors.Add($"At most {MaxNodes} elements.");
        }
        if (check.Fields > MaxFields)
        {
            check.Errors.Add($"At most {MaxFields} fields.");
        }
        foreach (var field in catalog.Fields.Where(f => f.Core && !check.Used.Contains(f.Key)))
        {
            check.Errors.Add($"Core field '{field.Key}' cannot be removed.");
        }
        return check.Errors;
    }

    private static void CheckChildren(JsonNode? node, int depth, string where, Check check)
    {
        if (node is not JsonArray children)
        {
            check.Errors.Add($"{where} needs a list of elements.");
            return;
        }
        if (children.Count > MaxChildren)
        {
            check.Errors.Add($"{where} has too many elements.");
        }
        foreach (var child in children)
        {
            CheckNode(child, depth, check);
        }
    }

    private static void CheckNode(JsonNode? node, int depth, Check check)
    {
        check.Nodes++;
        var obj = node as JsonObject;
        var type = obj is null ? null : StudioDocumentValidator.AsString(obj["type"]);

        switch (type)
        {
            case "field":
                check.Fields++;
                StudioDocumentValidator.ValidateField(obj, check.Catalog, check.Used, check.Errors, inTree: true);
                return;

            case "container":
                if (!CheckBox(obj!, ["type", "id", "layout", "columns", "gap", "children"], depth, check))
                {
                    return;
                }
                CheckLayout(obj!, check);
                CheckChildren(obj!["children"], depth + 1, $"Element '{Id(obj!)}'", check);
                return;

            case "fieldset":
                if (!CheckBox(obj!, ["type", "id", "legend", "layout", "columns", "gap", "collapsible", "collapsed", "children"], depth, check))
                {
                    return;
                }
                if (!StudioDocumentValidator.IsText(obj!["legend"]))
                {
                    check.Errors.Add($"Element '{Id(obj!)}' needs a legend of up to {StudioDocumentValidator.MaxText} characters.");
                }
                CheckLayout(obj!, check);
                CheckFlag(obj!, "collapsible", check);
                CheckFlag(obj!, "collapsed", check);
                CheckChildren(obj!["children"], depth + 1, $"Element '{Id(obj!)}'", check);
                return;

            case "tabs":
                if (!CheckBox(obj!, ["type", "id", "tabs"], depth, check))
                {
                    return;
                }
                CheckItems(obj!, "tabs", ["id", "label", "children"], "label", depth, check);
                return;

            case "accordion":
                if (!CheckBox(obj!, ["type", "id", "allowMultiple", "sections"], depth, check))
                {
                    return;
                }
                CheckFlag(obj!, "allowMultiple", check);
                CheckItems(obj!, "sections", ["id", "title", "expanded", "children"], "title", depth, check);
                return;

            default:
                check.Errors.Add($"Unknown element type '{type}'.");
                return;
        }
    }

    private static string? Id(JsonObject node) => StudioDocumentValidator.AsString(node["id"]);

    private static bool CheckBox(JsonObject node, string[] allowed, int depth, Check check)
    {
        StudioDocumentValidator.UnknownKeys(node, allowed, $"element '{Id(node)}'", check.Errors);
        CheckId(node["id"], check);
        if (depth > MaxDepth)
        {
            check.Errors.Add($"Elements are nested too deep (at most {MaxDepth} levels).");
            return false;
        }
        return true;
    }

    private static void CheckId(JsonNode? idNode, Check check)
    {
        var id = StudioDocumentValidator.AsString(idNode);
        if (id is null || !StudioDocumentValidator.SectionIdPattern().IsMatch(id))
        {
            check.Errors.Add($"Element id '{id}' is not valid.");
        }
        else if (!check.Ids.Add(id))
        {
            check.Errors.Add($"Element id '{id}' is repeated.");
        }
    }

    private static void CheckItems(JsonObject owner, string listKey, string[] allowed, string titleKey, int depth, Check check)
    {
        var ownerId = Id(owner);
        if (owner[listKey] is not JsonArray { Count: > 0 } items || items.Count > MaxItems)
        {
            check.Errors.Add($"Element '{ownerId}' needs between 1 and {MaxItems} items.");
            return;
        }

        foreach (var node in items)
        {
            var item = node as JsonObject ?? new JsonObject();
            var itemId = Id(item);
            StudioDocumentValidator.UnknownKeys(item, allowed, $"item '{itemId}' of '{ownerId}'", check.Errors);
            CheckId(item["id"], check);
            if (!StudioDocumentValidator.IsText(item[titleKey]))
            {
                check.Errors.Add($"Item '{itemId}' of '{ownerId}' needs a {titleKey} of up to {StudioDocumentValidator.MaxText} characters.");
            }
            if (item["expanded"] is not null)
            {
                CheckFlag(item, "expanded", check);
            }
            CheckChildren(item["children"], depth + 1, $"Item '{itemId}'", check);
        }
    }

    private static void CheckLayout(JsonObject node, Check check)
    {
        var id = Id(node);
        if (node["layout"] is { } layout && !(StudioDocumentValidator.AsString(layout) is { } name && Layouts.Contains(name)))
        {
            check.Errors.Add($"'layout' on '{id}' must be one of: {string.Join(", ", Layouts)}.");
        }
        if (node["columns"] is { } columns && !IsWhole(columns, 1, MaxColumns))
        {
            check.Errors.Add($"'columns' on '{id}' must be a whole number from 1 to {MaxColumns}.");
        }
        if (node["gap"] is { } gap && !IsWhole(gap, 0, MaxGap))
        {
            check.Errors.Add($"'gap' on '{id}' must be a whole number from 0 to {MaxGap}.");
        }
    }

    private static void CheckFlag(JsonObject node, string prop, Check check)
    {
        if (node[prop] is { } value && !(value is JsonValue v && v.TryGetValue<bool>(out _)))
        {
            check.Errors.Add($"'{prop}' on '{Id(node)}' must be true or false.");
        }
    }

    internal static bool IsWhole(JsonNode node, int min, int max) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) && number >= min && number <= max;
}
