namespace IMed.Api.Domain.Catalogs;

/// <summary>
/// Slice local del catálogo de medicamentos HN para el typeahead de recetas
/// (Épica K1 — D-Hito2-05). El dueño confirmó tener CIE-11 y LOINC en CSV
/// pero NO medicamentos, así que esto es un mock curado con ~50 medicamentos
/// frecuentes en consulta ambulatoria.
///
/// <para>Códigos del CodeSystem WHO ATC (<see cref="Terminology.CodeSystems.Atc"/>):
/// portable, libre, alineado con HL7 (FHIR ofrece value-sets ATC pre-armados).
/// Cuando el dueño obtenga el catálogo oficial HN (o RxNorm), migrar es un
/// importer + columna adicional sin cambio de schema mayor.</para>
///
/// <para><see cref="Category"/> = grupo terapéutico simplificado (analgésicos,
/// antibióticos, antihipertensivos, etc.) para filtrado del typeahead. Hito
/// 2 no requiere los Level-1 oficiales del ATC.</para>
/// </summary>
public sealed class MedicationCatalogEntry
{
    /// <summary>Código ATC. Clave primaria. Ej: "N02BE01" (paracetamol).</summary>
    public string AtcCode { get; private set; } = string.Empty;

    /// <summary>Nombre genérico (DCI). Ej: "Paracetamol".</summary>
    public string NameGeneric { get; private set; } = string.Empty;

    /// <summary>Nombre comercial común en HN. Opcional. Ej: "Acetaminofén / Tempra".</summary>
    public string? NameCommercial { get; private set; }

    /// <summary>Presentación habitual. Ej: "tab 500 mg", "susp 100 mg/5 ml".</summary>
    public string? Presentation { get; private set; }

    /// <summary>Categoría logical para filtrado UI. Ej: "analgesico", "antibiotico".</summary>
    public string Category { get; private set; } = "other";

    /// <summary>Permite deprecar sin borrar para preservar referencias en MedicationRequest históricos.</summary>
    public bool Active { get; private set; } = true;

    private MedicationCatalogEntry() { }

    public static MedicationCatalogEntry Create(
        string atcCode,
        string nameGeneric,
        string? nameCommercial = null,
        string? presentation = null,
        string category = "other",
        bool active = true)
    {
        if (string.IsNullOrWhiteSpace(atcCode))
        {
            throw new ArgumentException("AtcCode es obligatorio.", nameof(atcCode));
        }
        if (string.IsNullOrWhiteSpace(nameGeneric))
        {
            throw new ArgumentException("NameGeneric es obligatorio.", nameof(nameGeneric));
        }
        return new MedicationCatalogEntry
        {
            AtcCode = atcCode.Trim(),
            NameGeneric = nameGeneric.Trim(),
            NameCommercial = string.IsNullOrWhiteSpace(nameCommercial) ? null : nameCommercial.Trim(),
            Presentation = string.IsNullOrWhiteSpace(presentation) ? null : presentation.Trim(),
            Category = string.IsNullOrWhiteSpace(category) ? "other" : category.Trim(),
            Active = active,
        };
    }
}
