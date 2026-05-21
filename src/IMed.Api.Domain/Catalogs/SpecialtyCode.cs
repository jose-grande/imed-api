namespace IMed.Api.Domain.Catalogs;

/// <summary>
/// Catálogo de especialidades médicas (SNOMED CT). Tabla de referencia
/// compartida (no por org). Hito 1 trae ~12-15 especialidades comunes;
/// Hito 2 lo extiende.
/// </summary>
public sealed class SpecialtyCode
{
    /// <summary>SNOMED CT code. Ej: "394814009" (Medicina General y Familia).</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Display en español. Ej: "Medicina General y de Familia".</summary>
    public string DisplayEs { get; private set; } = string.Empty;

    /// <summary>System URL del code. Default SNOMED CT. Permite mezclar futuros catálogos.</summary>
    public string System { get; private set; } = "http://snomed.info/sct";

    /// <summary>Si está activa. Permite deprecar sin borrar (preserva referencias en PractitionerRole).</summary>
    public bool Active { get; private set; }

    private SpecialtyCode() { }

    public static SpecialtyCode Create(string code, string displayEs, string? system = null, bool active = true)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(displayEs))
        {
            throw new ArgumentException("DisplayEs es obligatorio.", nameof(displayEs));
        }
        return new SpecialtyCode
        {
            Code = code.Trim(),
            DisplayEs = displayEs.Trim(),
            System = string.IsNullOrWhiteSpace(system) ? "http://snomed.info/sct" : system.Trim(),
            Active = active
        };
    }
}
