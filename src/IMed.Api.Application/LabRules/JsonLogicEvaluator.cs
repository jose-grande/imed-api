using System.Globalization;
using System.Text.Json;

namespace IMed.Api.Application.LabRules;

/// <summary>
/// Evaluador JSON Logic (subset) para reglas de laboratorio (Épica LIS-5, L5-D1).
/// Soporta: <c>var</c>, comparaciones (==, !=, &gt;, &gt;=, &lt;, &lt;=), lógicos
/// (and, or, !), aritmética (+, -, *, /), <c>abs</c>, <c>in</c>. Literales:
/// número, string, bool, null. Sin dependencias externas.
/// </summary>
public static class JsonLogicEvaluator
{
    /// <summary>Evalúa la regla y devuelve si "dispara" (truthiness del resultado).</summary>
    public static bool Evaluate(JsonElement logic, IReadOnlyDictionary<string, object?> data) =>
        IsTruthy(Apply(logic, data));

    /// <summary>Evalúa una regla serializada como string JSON. <c>false</c> si el JSON es inválido.</summary>
    public static bool Evaluate(string logicJson, IReadOnlyDictionary<string, object?> data)
    {
        try
        {
            using var doc = JsonDocument.Parse(logicJson);
            return Evaluate(doc.RootElement, data);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static object? Apply(JsonElement node, IReadOnlyDictionary<string, object?> data)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    return Operate(prop.Name, prop.Value, data); // primer (único) operador
                }
                return null;
            case JsonValueKind.Array:
                return node.EnumerateArray().Select(e => Apply(e, data)).ToList();
            case JsonValueKind.Number:
                return node.GetDouble();
            case JsonValueKind.String:
                return node.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static object? Operate(string op, JsonElement argsNode, IReadOnlyDictionary<string, object?> data)
    {
        // "var" se maneja aparte: su primer argumento es un nombre literal, no una expresión a aplicar.
        if (op == "var")
        {
            var name = argsNode.ValueKind == JsonValueKind.Array
                ? (argsNode.GetArrayLength() > 0 ? argsNode[0].GetString() : null)
                : argsNode.GetString();
            object? def = argsNode.ValueKind == JsonValueKind.Array && argsNode.GetArrayLength() > 1
                ? Apply(argsNode[1], data)
                : null;
            if (name is not null && data.TryGetValue(name, out var v))
            {
                return v;
            }
            return def;
        }

        var args = argsNode.ValueKind == JsonValueKind.Array
            ? argsNode.EnumerateArray().Select(e => Apply(e, data)).ToList()
            : new List<object?> { Apply(argsNode, data) };

        switch (op)
        {
            case "==": return LooseEquals(Arg(args, 0), Arg(args, 1));
            case "!=": return !LooseEquals(Arg(args, 0), Arg(args, 1));
            case ">": return Compare(args) > 0;
            case ">=": return Compare(args) >= 0;
            case "<": return Compare(args) < 0;
            case "<=": return Compare(args) <= 0;
            case "!": return !IsTruthy(Arg(args, 0));
            case "and": return args.All(IsTruthy);
            case "or": return args.Any(IsTruthy);
            case "+": return args.Sum(ToDouble);
            case "-":
                if (args.Count == 1)
                {
                    return -ToDouble(args[0]);
                }
                return ToDouble(args[0]) - args.Skip(1).Sum(ToDouble);
            case "*": return args.Aggregate(1.0, (acc, x) => acc * ToDouble(x));
            case "/": return ToDouble(Arg(args, 0)) / ToDouble(Arg(args, 1));
            case "abs": return Math.Abs(ToDouble(Arg(args, 0)));
            case "in":
                var needle = Arg(args, 0);
                return Arg(args, 1) is List<object?> haystack && haystack.Any(x => LooseEquals(x, needle));
            default:
                return null;
        }
    }

    private static object? Arg(List<object?> args, int i) => i < args.Count ? args[i] : null;

    // Comparación numérica: devuelve -1/0/1; si algún operando no es numérico → 0 con comparación falsa.
    private static int Compare(List<object?> args)
    {
        if (args.Count < 2 || args[0] is null || args[1] is null)
        {
            return int.MinValue; // fuerza que >,>=,<,<= den false
        }
        return ToDouble(args[0]).CompareTo(ToDouble(args[1]));
    }

    private static bool LooseEquals(object? a, object? b)
    {
        if (a is null && b is null)
        {
            return true;
        }
        if (a is null || b is null)
        {
            return false;
        }
        if (a is bool || b is bool)
        {
            return IsTruthy(a) == IsTruthy(b);
        }
        if (IsNumeric(a) && IsNumeric(b))
        {
            return Math.Abs(ToDouble(a) - ToDouble(b)) < 1e-9;
        }
        return string.Equals(a.ToString(), b.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object? v) => v is double or int or long or decimal or float;

    private static double ToDouble(object? v) => v switch
    {
        double d => d,
        int i => i,
        long l => l,
        decimal m => (double)m,
        float f => f,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
        _ => double.NaN,
    };

    private static bool IsTruthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };
}
