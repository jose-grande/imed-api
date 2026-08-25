namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Un tributo aplicable a un cobrable (FSF bloque 1, núcleo agnóstico).
///
/// <para><b>Por qué la tasa es dato y no constante.</b> En una clínica hondureña conviven exentos
/// y gravados en la misma factura: la consulta y el hemograma no pagan ISV, el tratamiento estético
/// paga 15%, y la farmacia vende medicamento exento junto a cosmético gravado. Un sistema con la
/// tasa quemada obliga a facturar por separado o a declarar mal; las dos salidas son malas.</para>
/// </summary>
public sealed class TaxCode
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Código corto, único por organización. Ej.: <c>ISV15</c>, <c>EXENTO</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Porcentaje, no fracción: 15% se guarda como <c>15.0000</c>. <c>numeric(7,4)</c> deja lugar
    /// a tasas con decimales sin arrastrar el error de un binario flotante al totalizar.
    /// </summary>
    public decimal Rate { get; private set; }

    public TaxTreatment Treatment { get; private set; }
    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private TaxCode() { }

    public static TaxCode Create(
        Guid organizationId,
        string code,
        string name,
        decimal rate,
        TaxTreatment treatment,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código del tributo es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del tributo es obligatorio.", nameof(name));
        }
        if (rate < 0 || rate > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(rate), "La tasa va de 0 a 100.");
        }

        // Lo que no se grava no lleva tasa. Un "exento al 15%" es una fila que va a producir un
        // impuesto cobrado sobre algo que la ley no grava, y eso se descubre en la declaración.
        if (treatment != TaxTreatment.Taxed && rate != 0)
        {
            throw new ArgumentException(
                $"Un tributo '{treatment}' no lleva tasa: {rate} no corresponde.", nameof(rate));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new TaxCode
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Rate = rate,
            Treatment = treatment,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Impuesto sobre una base. Redondea a 2 decimales, que es la moneda.</summary>
    public decimal TaxOn(decimal baseAmount) =>
        Treatment == TaxTreatment.Taxed
            ? Math.Round(baseAmount * Rate / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
