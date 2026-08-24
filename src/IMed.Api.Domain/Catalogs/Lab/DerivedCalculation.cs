namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Fórmula calculada (Épica LIS-1, L1-D6). Define un resultado derivado de otros
/// (LDL Friedewald, eGFR CKD-EPI, anion gap). LIS-1 solo la <b>define</b>; la
/// evaluación corre cuando hay resultados (LIS-4/LIS-5).
///
/// <para><see cref="FormulaJson"/> = expresión JSON Logic o fórmula serializada.
/// <see cref="InputsJson"/> = códigos de pruebas requeridas (jsonb array).</para>
/// </summary>
public sealed class DerivedCalculation
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código de la prueba de salida (resultado calculado). Ej: "LDL_CALC".</summary>
    public string OutputTestCode { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>Fórmula (JSON Logic / expresión) serializada como jsonb.</summary>
    public string FormulaJson { get; private set; } = "{}";

    /// <summary>Códigos de pruebas de entrada, jsonb array. Ej: ["CHOL","HDL","TRIG"].</summary>
    public string InputsJson { get; private set; } = "[]";

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }

    private DerivedCalculation() { }

    public static DerivedCalculation Create(
        string outputTestCode,
        string name,
        string formulaJson,
        string inputsJson,
        Guid? organizationId = null,
        bool active = true,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(outputTestCode))
        {
            throw new ArgumentException("OutputTestCode es obligatorio.", nameof(outputTestCode));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        if (string.IsNullOrWhiteSpace(formulaJson))
        {
            throw new ArgumentException("FormulaJson es obligatoria.", nameof(formulaJson));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new DerivedCalculation
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            OutputTestCode = outputTestCode.Trim(),
            Name = name.Trim(),
            FormulaJson = formulaJson,
            InputsJson = string.IsNullOrWhiteSpace(inputsJson) ? "[]" : inputsJson,
            Active = active,
            CreatedAt = now,
        };
    }
}
