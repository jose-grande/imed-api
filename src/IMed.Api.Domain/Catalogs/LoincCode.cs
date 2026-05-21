namespace IMed.Api.Domain.Catalogs;

/// <summary>
/// Catálogo LOINC — un concepto del LOINC standard release.
///
/// Tabla de referencia compartida (no por org). Source: LOINC.org release ZIP
/// importado por <c>LoincStandardImporter</c>. System URI canónico:
/// <c>http://loinc.org</c> (ver <c>Domain.Terminology.CodeSystems.Loinc</c>).
/// License LOINC (free + atribución).
///
/// <see cref="Properties"/> contiene metadata específica de LOINC: class,
/// class-type, status, system, property, scale-type, time-aspect,
/// method-type, short-name, is-panel, etc. Plus flag <c>translation-pending</c>
/// para concepts que no tienen traducción al español aún.
/// </summary>
public sealed class LoincCode
{
    /// <summary>Código LOINC. Ej: "718-7" (hemoglobina), "24356-8" (hemograma).</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display en español (de la linguistic variant primaria — esMX28 por default). Fallback al inglés si no hay traducción.</summary>
    public string DisplayEs { get; private set; } = string.Empty;

    /// <summary>Display en inglés (LONG_COMMON_NAME del main file).</summary>
    public string? DisplayEn { get; private set; }

    /// <summary>
    /// Metadata del concepto. Keys típicas para LOINC: <c>class</c>,
    /// <c>class-type</c>, <c>status</c>, <c>system</c>, <c>property</c>,
    /// <c>scale-type</c>, <c>time-aspect</c>, <c>method-type</c>,
    /// <c>short-name</c>, <c>is-panel</c>, <c>order-obs</c>,
    /// <c>translation-pending</c>.
    /// </summary>
    public Dictionary<string, string> Properties { get; private set; } = new();

    /// <summary>Si está activo. LOINC marca DEPRECATED algunos códigos sin borrarlos.</summary>
    public bool Active { get; private set; }

    private LoincCode() { }

    public static LoincCode Create(
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
        return new LoincCode
        {
            Code = code.Trim(),
            DisplayEs = displayEs.Trim(),
            DisplayEn = string.IsNullOrWhiteSpace(displayEn) ? null : displayEn.Trim(),
            Properties = properties ?? new Dictionary<string, string>(),
            Active = active
        };
    }
}
