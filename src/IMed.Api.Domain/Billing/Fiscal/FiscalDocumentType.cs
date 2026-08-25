namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Clase de documento legal (FSF bloque 1, núcleo agnóstico): factura, nota de crédito, nota de
/// débito, recibo, proforma, comprobante de retención.
///
/// <para><b>Por qué es tabla y no enum.</b> Cada país tiene su juego de documentos y su código de
/// dos dígitos dentro del número fiscal. Un enum obligaría a recompilar para operar en otro
/// régimen, que es justamente lo que el núcleo agnóstico viene a evitar.</para>
/// </summary>
public sealed class FiscalDocumentType
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Clave interna estable. Ej.: <c>factura</c>, <c>nota_credito</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Los dos dígitos que este tipo ocupa en el número fiscal (segmento 3 de
    /// <c>NNN-NNN-NN-NNNNNNNN</c> en Honduras). Es dato del régimen, no del producto.
    /// </summary>
    public string DocumentCode { get; private set; } = string.Empty;

    /// <summary>¿Mueve el saldo de la cuenta? Una proforma no.</summary>
    public bool AffectsReceivable { get; private set; }

    /// <summary>
    /// <c>+1</c> suma al saldo (factura), <c>-1</c> resta (nota de crédito). Se guarda en vez de
    /// deducirse del código porque el signo es lo que hace cuadrar un estado de cuenta, y deducirlo
    /// de una cadena de texto es cómo se cuela una nota de crédito que suma.
    /// </summary>
    public short Sign { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private FiscalDocumentType() { }

    public static FiscalDocumentType Create(
        Guid organizationId,
        string code,
        string name,
        string documentCode,
        bool affectsReceivable,
        short sign,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código del tipo de documento es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del tipo de documento es obligatorio.", nameof(name));
        }
        if (sign is not (1 or -1 or 0))
        {
            throw new ArgumentOutOfRangeException(nameof(sign), "El signo es +1, -1 o 0.");
        }
        if (affectsReceivable && sign == 0)
        {
            throw new ArgumentException(
                "Un documento que mueve el saldo necesita signo.", nameof(sign));
        }

        var trimmed = (documentCode ?? string.Empty).Trim();
        if (trimmed.Length != 2 || !trimmed.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "El código de documento son exactamente 2 dígitos.", nameof(documentCode));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FiscalDocumentType
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            DocumentCode = trimmed,
            AffectsReceivable = affectsReceivable,
            Sign = sign,
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
