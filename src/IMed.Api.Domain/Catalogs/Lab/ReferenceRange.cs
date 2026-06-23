namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Rango de referencia maestro de una prueba (Épica LIS-1, L1-D5).
///
/// <para>FHIR embebe el rango por resultado (<c>Observation.referenceRange</c>),
/// pero el LIS necesita la fuente de verdad para seleccionar el rango correcto
/// según edad/sexo/embarazo y derivar los flags (H/L vs HH/LL).</para>
///
/// <para><see cref="Low"/>/<see cref="High"/> = rango normal (fuera ⇒ flag H/L).
/// <see cref="CriticalLow"/>/<see cref="CriticalHigh"/> = valor crítico
/// (fuera ⇒ flag HH/LL, dispara notificación en LIS-7). Distinción del doc:
/// valor de referencia ≠ valor crítico.</para>
/// </summary>
public sealed class ReferenceRange
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    public Guid LabTestId { get; private set; }
    public ReferenceSex Sex { get; private set; } = ReferenceSex.Any;

    /// <summary>Edad mínima en años (inclusive). Null = sin límite inferior.</summary>
    public int? AgeMinYears { get; private set; }

    /// <summary>Edad máxima en años (inclusive). Null = sin límite superior.</summary>
    public int? AgeMaxYears { get; private set; }

    /// <summary>Aplica a embarazo. Null = indistinto.</summary>
    public bool? Pregnancy { get; private set; }

    /// <summary>Método analítico (los rangos varían por método). Opcional.</summary>
    public string? Method { get; private set; }

    public decimal? Low { get; private set; }
    public decimal? High { get; private set; }
    public decimal? CriticalLow { get; private set; }
    public decimal? CriticalHigh { get; private set; }
    public string? Unit { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ReferenceRange() { }

    public static ReferenceRange Create(
        Guid labTestId,
        ReferenceSex sex = ReferenceSex.Any,
        int? ageMinYears = null,
        int? ageMaxYears = null,
        bool? pregnancy = null,
        string? method = null,
        decimal? low = null,
        decimal? high = null,
        decimal? criticalLow = null,
        decimal? criticalHigh = null,
        string? unit = null,
        Guid? organizationId = null,
        TimeProvider? timeProvider = null)
    {
        if (labTestId == Guid.Empty)
        {
            throw new ArgumentException("LabTestId es obligatorio.", nameof(labTestId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new ReferenceRange
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LabTestId = labTestId,
            Sex = sex,
            AgeMinYears = ageMinYears,
            AgeMaxYears = ageMaxYears,
            Pregnancy = pregnancy,
            Method = string.IsNullOrWhiteSpace(method) ? null : method.Trim(),
            Low = low,
            High = high,
            CriticalLow = criticalLow,
            CriticalHigh = criticalHigh,
            Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(
        ReferenceSex sex,
        int? ageMinYears,
        int? ageMaxYears,
        bool? pregnancy,
        string? method,
        decimal? low,
        decimal? high,
        decimal? criticalLow,
        decimal? criticalHigh,
        string? unit,
        TimeProvider? timeProvider = null)
    {
        Sex = sex;
        AgeMinYears = ageMinYears;
        AgeMaxYears = ageMaxYears;
        Pregnancy = pregnancy;
        Method = string.IsNullOrWhiteSpace(method) ? null : method.Trim();
        Low = low;
        High = high;
        CriticalLow = criticalLow;
        CriticalHigh = criticalHigh;
        Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
