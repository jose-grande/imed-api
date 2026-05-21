namespace IMed.Api.Domain.Catalogs;

/// <summary>
/// Slice local de alergenos comunes para el typeahead de preclínica (Épica J).
/// Códigos del CodeSystem SNOMED CT (<see cref="Terminology.CodeSystems.SnomedCt"/>);
/// se persiste como entidad propia (no via <c>ITerminologyService</c>) porque
/// el volumen es pequeño (~25 entries) y el reuso de un catálogo SNOMED
/// completo es out-of-scope para el piloto. Si el catálogo crece a cientos de
/// entries, migrar a <c>INamedTerminologyCatalogReader</c> con tsv como ICD-11
/// es un cambio aislado documentado en EPICA-J.
///
/// <para><see cref="Category"/> facilita el filtrado del typeahead (alergias
/// alimentarias vs. medicamentosas vs. ambientales) — útil cuando el set crece.
/// Valores soportados al inicio: <c>food</c>, <c>medication</c>,
/// <c>environment</c>, <c>biologic</c>, <c>other</c>.</para>
/// </summary>
public sealed class AllergyCatalogEntry
{
    /// <summary>Código SNOMED CT. Clave primaria. Ej: "418689008".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display en español. Ej: "Alergia al polen de gramíneas".</summary>
    public string DisplayEs { get; private set; } = string.Empty;

    /// <summary>Categoría logical para filtrado UI.</summary>
    public string Category { get; private set; } = "other";

    /// <summary>Permite "deprecar" sin borrar para preservar referencias en AllergyIntolerance históricos.</summary>
    public bool Active { get; private set; } = true;

    private AllergyCatalogEntry() { }

    public static AllergyCatalogEntry Create(string code, string displayEs, string category = "other", bool active = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(displayEs))
        {
            throw new ArgumentException("DisplayEs es obligatorio.", nameof(displayEs));
        }
        return new AllergyCatalogEntry
        {
            Code = code.Trim(),
            DisplayEs = displayEs.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? "other" : category.Trim(),
            Active = active,
        };
    }
}
