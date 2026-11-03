namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Una unidad de laboratorio admitida: el código UCUM (lo que se guarda y se manda al
/// expediente FHIR) y la etiqueta (lo que ven el laboratorio, el médico y el paciente).
/// </summary>
public sealed record LabUnit(string Code, string Label, string Group);

/// <summary>
/// Unidades de laboratorio admitidas. <b>Lista fija</b>, sembrada por nosotros: las pruebas y los
/// rangos de referencia solo pueden usar una de estas.
///
/// <para>Antes la unidad era texto libre con sugerencias, y la mayoría de las sugerencias no
/// eran UCUM (<c>mIU/L</c>, <c>x10^3/uL</c>, <c>mEq/L</c>). HAPI valida las unidades de cada
/// resultado contra UCUM y rechazaba el guardado (observación de staging: TSH en <c>mIU/L</c>).
/// Ahora en la base se guarda el <see cref="LabUnit.Code"/> y se muestra la
/// <see cref="LabUnit.Label"/>: el estándar en los datos, la notación de siempre en pantalla y
/// en el informe impreso.</para>
///
/// <para>Cada código se verificó contra el validador UCUM de HAPI. Para agregar una unidad,
/// verificarla de la misma forma (<c>POST /fhir/Observation/$validate</c>) antes de sumarla.
/// Una unidad sin forma UCUM directa usa una anotación entre llaves (<c>{copies}/mL</c>).</para>
/// </summary>
public static class LabUnits
{
    private const string Mass = "Concentración (masa)";
    private const string Molar = "Concentración (molar)";
    private const string Activity = "Actividad enzimática / hormonal";
    private const string Count = "Recuentos celulares";
    private const string Fraction = "Fracciones e índices";
    private const string Time = "Tiempos y coagulación";
    private const string Physiology = "Gases, función renal y osmolalidad";
    private const string Urine = "Orina";
    private const string Serology = "Microbiología y serología";

    /// <summary>En el orden en que se ofrecen.</summary>
    public static readonly IReadOnlyList<LabUnit> All =
    [
        new("g/dL", "g/dL", Mass),
        new("mg/dL", "mg/dL", Mass),
        new("ug/dL", "µg/dL", Mass),
        new("ng/dL", "ng/dL", Mass),
        new("g/L", "g/L", Mass),
        new("mg/L", "mg/L", Mass),
        new("ug/L", "µg/L", Mass),
        new("ng/L", "ng/L", Mass),
        new("ug/mL", "µg/mL", Mass),
        new("ng/mL", "ng/mL", Mass),
        new("pg/mL", "pg/mL", Mass),

        new("mol/L", "mol/L", Molar),
        new("mmol/L", "mmol/L", Molar),
        new("umol/L", "µmol/L", Molar),
        new("nmol/L", "nmol/L", Molar),
        new("pmol/L", "pmol/L", Molar),
        new("meq/L", "mEq/L", Molar),

        new("U/L", "U/L", Activity),
        new("U/mL", "U/mL", Activity),
        new("kU/L", "kU/L", Activity),
        new("[IU]/L", "IU/L", Activity),
        new("[IU]/mL", "IU/mL", Activity),
        new("k[IU]/L", "kIU/L", Activity),
        new("m[IU]/L", "mIU/L", Activity),
        new("m[IU]/mL", "mIU/mL", Activity),
        new("u[IU]/mL", "µIU/mL", Activity),

        new("10*3/uL", "×10³/µL", Count),
        new("10*6/uL", "×10⁶/µL", Count),
        new("10*9/L", "×10⁹/L", Count),
        new("10*12/L", "×10¹²/L", Count),
        new("/uL", "/µL", Count),
        new("{cells}/uL", "células/µL", Count),

        new("%", "%", Fraction),
        new("fL", "fL", Fraction),
        new("pg", "pg", Fraction),
        new("{ratio}", "ratio", Fraction),
        new("{index}", "índice", Fraction),

        new("s", "s", Time),
        new("min", "min", Time),
        new("{INR}", "INR", Time),
        new("mm/h", "mm/h", Time),

        new("mm[Hg]", "mmHg", Physiology),
        new("kPa", "kPa", Physiology),
        new("[pH]", "pH", Physiology),
        new("mL/min/{1.73_m2}", "mL/min/1.73m²", Physiology),
        new("mosm/kg", "mOsm/kg", Physiology),

        new("mg/(24.h)", "mg/24h", Urine),
        new("g/(24.h)", "g/24h", Urine),
        new("mL/(24.h)", "mL/24h", Urine),
        new("mg/g", "mg/g", Urine),
        new("/[HPF]", "/campo (alto poder)", Urine),
        new("/[LPF]", "/campo (bajo poder)", Urine),

        new("{copies}/mL", "copias/mL", Serology),
        new("{log_copies}/mL", "log10 copias/mL", Serology),
        new("{titer}", "título", Serology),
    ];

    private static readonly Dictionary<string, LabUnit> ByCode =
        All.ToDictionary(u => u.Code, StringComparer.Ordinal);

    /// <summary>La unidad con ese código UCUM exacto (UCUM distingue mayúsculas), o null.</summary>
    public static LabUnit? Find(string? code) =>
        code is not null && ByCode.TryGetValue(code.Trim(), out var unit) ? unit : null;

    /// <summary>¿Es un código de la lista? Vacío cuenta como "sin unidad", que es válido.</summary>
    public static bool IsAllowed(string? code) => string.IsNullOrWhiteSpace(code) || Find(code) is not null;

    /// <summary>La etiqueta para mostrar; un código fuera de la lista (dato viejo) se muestra tal cual.</summary>
    public static string? LabelFor(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : Find(code)?.Label ?? code.Trim();

    /// <summary>
    /// Normaliza y valida la unidad que llega de un formulario: null si está vacía, el código si es
    /// de la lista, y si no <see cref="ArgumentException"/> con un mensaje para el usuario.
    /// </summary>
    public static string? Require(string? code, string paramName)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }
        return Find(code)?.Code
            ?? throw new ArgumentException(
                $"La unidad '{code.Trim()}' no está en la lista de unidades admitidas. Elegí una de la lista.",
                paramName);
    }
}
