namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Tipos de recolección: <b>cómo</b> se obtiene la muestra del paciente (venopunción, hisopado…).
/// Es distinto del tipo de muestra, que es <b>qué</b> material se analiza (sangre total, suero,
/// orina…): una glucosa se recolecta por venopunción y se analiza en suero o plasma.
///
/// <para>Lista fija y no texto libre: era un campo libre y terminó con valores internos
/// (<c>blood</c>, <c>urine-spot</c>) que mezclaban las dos ideas. Se guarda el nombre tal cual,
/// que es lo que se muestra y lo que se imprime.</para>
/// </summary>
public static class LabCollectionMethods
{
    public const string Venipuncture = "Venopunción";
    public const string CapillaryPuncture = "Punción capilar";
    public const string SpontaneousVoiding = "Micción espontánea";
    public const string Urine24h = "Recolección de orina de 24 horas";
    public const string Catheter = "Recolección por sonda";
    public const string Direct = "Recolección directa";
    public const string Swab = "Hisopado";
    public const string Aspiration = "Aspiración";
    public const string Expectoration = "Expectoración";
    public const string LumbarPuncture = "Punción lumbar";
    public const string Biopsy = "Biopsia";
    public const string Masturbation = "Masturbación";

    /// <summary>En el orden en que se ofrecen.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Venipuncture, CapillaryPuncture, SpontaneousVoiding, Urine24h, Catheter, Direct,
        Swab, Aspiration, Expectoration, LumbarPuncture, Biopsy, Masturbation,
    ];

    /// <summary>El nombre de la lista que coincide con <paramref name="value"/> (sin distinguir mayúsculas), o null.</summary>
    public static string? Canonical(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : All.FirstOrDefault(m => string.Equals(m, value.Trim(), StringComparison.OrdinalIgnoreCase));
}
