namespace IMed.Api.Domain.Catalogs;

/// <summary>
/// Catálogo ICD-11 — un concepto del WHO MMS linearization.
///
/// Tabla de referencia compartida (no por org). Source: WHO Simple Tabulation
/// importado por <c>Icd11WhoSimpleTabulationImporter</c>. System URI canónico:
/// <c>http://id.who.int/icd11/mms</c> (ver <c>Domain.Terminology.CodeSystems.Icd11</c>).
///
/// <see cref="Properties"/> contiene metadata específica de ICD-11 — chapter,
/// parent-code, is-leaf, foundation-uri. Schema deliberadamente abierto vía
/// dictionary para que el shape no se rigidice antes de tiempo y para que
/// otros code systems (LOINC, SNOMED) puedan poblar sus propias keys sin
/// migración de schema.
/// </summary>
public sealed class Icd11Code
{
    /// <summary>Código ICD-11 (clave primaria). Ej: "1A00", "1A03.0", "BD01.Z".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display en español (Spanish MMS). Ej: "Cólera".</summary>
    public string DisplayEs { get; private set; } = string.Empty;

    /// <summary>Display en inglés (TitleEN del WHO TSV). Opcional.</summary>
    public string? DisplayEn { get; private set; }

    /// <summary>
    /// Metadata del concepto. Keys típicas para ICD-11: <c>chapter</c>,
    /// <c>parent-code</c>, <c>is-leaf</c>, <c>foundation-uri</c>.
    /// </summary>
    public Dictionary<string, string> Properties { get; private set; } = new();

    /// <summary>Si está activo. Permite "deprecar" sin borrar para preservar referencias históricas en Conditions.</summary>
    public bool Active { get; private set; }

    private Icd11Code() { }

    public static Icd11Code Create(
        string code,
        string displayEs,
        string? displayEn = null,
        Dictionary<string, string>? properties = null,
        bool active = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(displayEs))
        {
            throw new ArgumentException("DisplayEs es obligatorio.", nameof(displayEs));
        }
        return new Icd11Code
        {
            Code = code.Trim(),
            DisplayEs = displayEs.Trim(),
            DisplayEn = string.IsNullOrWhiteSpace(displayEn) ? null : displayEn.Trim(),
            Properties = properties ?? new Dictionary<string, string>(),
            Active = active
        };
    }
}
