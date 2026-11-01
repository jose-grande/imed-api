using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// Valida un documento del studio (<c>studio-v1</c>: secciones y filas, o <c>studio-v2</c>: árbol)
/// contra el catálogo del formulario.
/// Devuelve un mensaje por problema; una lista vacía significa que se puede publicar.
/// Son las mismas reglas y mensajes que el compilador del cliente.
/// </summary>
public static partial class StudioDocumentValidator
{
    public const string Kind = "studio-v1";
    public const string TreeKind = "studio-v2";
    public const int MaxSections = 20;
    public const int MaxRowsPerSection = 50;
    public const int MaxFieldsPerRow = 4;
    public const int MaxFields = 200;
    public const int MaxText = 200;

    private static readonly string[] TextProps = ["label", "placeholder", "helpText", "description", "onLabel", "offLabel"];

    [GeneratedRegex("^[a-z][a-z0-9_-]{0,39}$")]
    internal static partial Regex SectionIdPattern();

    [GeneratedRegex("^custom_[a-z0-9_]{1,40}$")]
    private static partial Regex CustomIdPattern();

    public static IReadOnlyList<string> Validate(JsonNode? document, FieldCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (document is JsonObject { } tree && AsString(tree["kind"]) == TreeKind)
        {
            return StudioTreeValidator.Validate(tree, catalog);
        }
        if (document is not JsonObject root || AsString(root["kind"]) != Kind)
        {
            return [$"kind must be '{Kind}' or '{TreeKind}'."];
        }
        if (root["sections"] is not JsonArray { Count: > 0 } sections)
        {
            return ["The form needs at least one section."];
        }

        var errors = new List<string>();
        if (sections.Count > MaxSections)
        {
            errors.Add($"At most {MaxSections} sections.");
        }

        var sectionIds = new HashSet<string>();
        var used = new HashSet<string>();
        var total = 0;

        foreach (var node in sections)
        {
            if (node is not JsonObject section)
            {
                errors.Add("Each section must be an object.");
                continue;
            }

            var id = AsString(section["id"]);
            if (id is null || !SectionIdPattern().IsMatch(id))
            {
                errors.Add($"Section id '{id}' is not valid.");
            }
            else if (!sectionIds.Add(id))
            {
                errors.Add($"Section id '{id}' is repeated.");
            }

            if (!IsText(section["label"]))
            {
                errors.Add($"Section '{id}' needs a label of up to {MaxText} characters.");
            }
            UnknownKeys(section, ["id", "label", "rows"], $"section '{id}'", errors);

            if (section["rows"] is not JsonArray { Count: > 0 } rows)
            {
                errors.Add($"Section '{id}' needs at least one row.");
                continue;
            }
            if (rows.Count > MaxRowsPerSection)
            {
                errors.Add($"Section '{id}' has too many rows.");
            }

            foreach (var rowNode in rows)
            {
                if (rowNode is not JsonArray { Count: > 0 } row || row.Count > MaxFieldsPerRow)
                {
                    errors.Add($"A row in '{id}' must have between 1 and {MaxFieldsPerRow} fields.");
                    continue;
                }
                foreach (var placed in row)
                {
                    total++;
                    ValidateField(placed, catalog, used, errors);
                }
            }
        }

        if (total > MaxFields)
        {
            errors.Add($"At most {MaxFields} fields.");
        }

        foreach (var field in catalog.Fields.Where(f => f.Core && !used.Contains(f.Key)))
        {
            errors.Add($"Core field '{field.Key}' cannot be removed.");
        }

        return errors;
    }

    internal static void ValidateField(JsonNode? node, FieldCatalog catalog, HashSet<string> used, List<string> errors, bool inTree = false)
    {
        var key = node is JsonObject placed ? AsString(placed["field"]) : null;
        var def = key is null ? null : catalog.Find(key);
        if (node is not JsonObject obj || def is null)
        {
            errors.Add($"Field '{key}' is not in the catalog.");
            return;
        }

        UnknownKeys(obj, inTree ? ["type", "field", "id", "overrides", "span"] : ["field", "id", "overrides"], $"field '{key}'", errors);

        var id = AsString(obj["id"]);
        var name = def.Custom ? id : def.Key;

        if (def.Custom)
        {
            if (id is null || !CustomIdPattern().IsMatch(id))
            {
                errors.Add($"Custom field id '{id}' must look like custom_name.");
            }
        }
        else if (obj["id"] is not null)
        {
            errors.Add($"Field '{def.Key}' is not custom and cannot have an id.");
        }

        if (name is not null && !used.Add(name))
        {
            errors.Add($"Field '{name}' is used more than once.");
        }

        if (obj["span"] is { } span && !StudioTreeValidator.IsWhole(span, 1, StudioTreeValidator.MaxSpan))
        {
            errors.Add($"'span' on '{name}' must be a whole number from 1 to {StudioTreeValidator.MaxSpan}.");
        }

        if (obj["overrides"] is JsonObject overrides)
        {
            foreach (var (prop, value) in overrides)
            {
                if (!def.Editable.Contains(prop))
                {
                    errors.Add($"'{prop}' cannot be changed on field '{name}'.");
                }
                else if (prop == "required")
                {
                    if (value is not JsonValue v || !v.TryGetValue<bool>(out _))
                    {
                        errors.Add($"'required' on '{name}' must be true or false.");
                    }
                }
                else if (TextProps.Contains(prop) && !IsText(value, nonEmpty: prop == "label"))
                {
                    errors.Add($"'{prop}' on '{name}' must be text of up to {MaxText} characters.");
                }
            }
        }
    }

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal static bool IsText(JsonNode? node, bool nonEmpty = true)
    {
        var text = AsString(node);
        return text is not null
            && text.Length <= MaxText
            && (!nonEmpty || !string.IsNullOrWhiteSpace(text));
    }

    internal static void UnknownKeys(JsonObject target, string[] allowed, string where, List<string> errors)
    {
        foreach (var (key, _) in target)
        {
            if (!allowed.Contains(key))
            {
                errors.Add($"Unknown property '{key}' in {where}.");
            }
        }
    }
}
