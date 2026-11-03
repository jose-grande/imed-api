namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>Subárea del informe dentro de un área (Hematología → Serie roja).</summary>
public sealed record LabSubsection(LabDepartment Department, string Code, string Label);

/// <summary>
/// Áreas y subáreas del informe de resultados. <b>Lista fija</b>, sembrada por nosotros.
///
/// <para>Observación de staging: el informe se divide por áreas y algunas áreas se subdividen por
/// proceso (Hematología en series y tiempos de coagulación; Uroanálisis y Parasitología en examen
/// físico, químico y microscópico). La subárea va en la <b>prueba</b> y no en el panel porque
/// cruza paneles: un TP pedido solo también cae en Hematología → Tiempos de coagulación.</para>
///
/// <para>El orden de <see cref="AreaOrder"/> y de <see cref="All"/> es el del informe impreso.</para>
/// </summary>
public static class LabSubsections
{
    /// <summary>Orden de las áreas en el informe.</summary>
    public static readonly IReadOnlyList<LabDepartment> AreaOrder =
    [
        LabDepartment.Hematology,
        LabDepartment.Chemistry,
        LabDepartment.Immunology,
        LabDepartment.Urinalysis,
        LabDepartment.Parasitology,
        LabDepartment.Microbiology,
        LabDepartment.Molecular,
        LabDepartment.Other,
    ];

    /// <summary>Título del área en el informe y en el catálogo.</summary>
    public static string AreaLabel(LabDepartment department) => department switch
    {
        LabDepartment.Hematology => "Hematología",
        LabDepartment.Chemistry => "Química clínica",
        LabDepartment.Immunology => "Inmunología",
        LabDepartment.Urinalysis => "Uroanálisis",
        LabDepartment.Parasitology => "Parasitología",
        LabDepartment.Microbiology => "Microbiología",
        LabDepartment.Molecular => "Biología molecular",
        _ => "Otros",
    };

    /// <summary>Subáreas, agrupadas por área y en el orden en que se imprimen.</summary>
    public static readonly IReadOnlyList<LabSubsection> All =
    [
        new(LabDepartment.Hematology, "HEM_RED", "Serie roja"),
        new(LabDepartment.Hematology, "HEM_WHITE", "Serie blanca"),
        new(LabDepartment.Hematology, "HEM_PLATELET", "Serie plaquetaria"),
        new(LabDepartment.Hematology, "HEM_COAG", "Tiempos de coagulación"),

        new(LabDepartment.Urinalysis, "UA_PHYSICAL", "Examen físico"),
        new(LabDepartment.Urinalysis, "UA_CHEMICAL", "Examen químico"),
        new(LabDepartment.Urinalysis, "UA_MICROSCOPIC", "Examen microscópico"),

        new(LabDepartment.Parasitology, "PAR_PHYSICAL", "Examen físico"),
        new(LabDepartment.Parasitology, "PAR_MICROSCOPIC", "Examen microscópico"),
    ];

    /// <summary>La subárea con ese código, si pertenece a <paramref name="department"/>.</summary>
    public static LabSubsection? Find(LabDepartment department, string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : All.FirstOrDefault(s => s.Department == department
                                      && string.Equals(s.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Posición de impresión de la subárea (las pruebas sin subárea van primero).</summary>
    public static int OrderOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return -1;
        }
        for (var i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i].Code, code, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Normaliza la subárea que llega de un formulario: null si está vacía, el código si es de
    /// esa área, y si no <see cref="ArgumentException"/> con un mensaje para el usuario.
    /// </summary>
    public static string? Require(LabDepartment department, string? code, string paramName)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        return Find(department, code)?.Code
            ?? throw new ArgumentException(
                $"La subárea '{code.Trim()}' no corresponde al área {AreaLabel(department)}. Elegí una de la lista.",
                paramName);
    }
}
