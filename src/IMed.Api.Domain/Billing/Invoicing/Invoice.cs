using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Domain.Billing.Invoicing;

/// <summary>
/// El documento <b>comercial</b> (FSF §7).
///
/// <para><b>Por qué va aparte del fiscal.</b> Una cuenta puede generar una factura al paciente por
/// el copago y otra a la aseguradora por el resto: dos documentos fiscales, distinto comprador,
/// mismo episodio. Y una proforma es factura comercial <em>sin</em> documento fiscal. Mezclarlos
/// es lo que impide operar en un país con control de numeración.</para>
/// </summary>
public sealed class Invoice
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid BillToPartyId { get; private set; }
    public BillToKind BillToKind { get; private set; }

    public DateOnly IssueDate { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public string Currency { get; private set; } = "HNL";

    public decimal Subtotal { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal TaxTotal { get; private set; }
    public decimal GrandTotal { get; private set; }

    /// <summary>Lo que falta cobrar. Arranca igual al total.</summary>
    public decimal Balance { get; private set; }

    public InvoiceStatus Status { get; private set; }

    /// <summary>Null hasta timbrar. Una proforma no lo tiene nunca.</summary>
    public Guid? FiscalDocumentId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private readonly List<InvoiceLine> _lines = [];
    public IReadOnlyList<InvoiceLine> Lines => _lines;

    private Invoice() { }

    public static Invoice Draft(
        Guid organizationId,
        Guid accountId,
        Guid billToPartyId,
        DateOnly issueDate,
        BillToKind billToKind = BillToKind.Patient,
        string currency = "HNL",
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new Invoice
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            AccountId = accountId,
            BillToPartyId = billToPartyId,
            BillToKind = billToKind,
            IssueDate = issueDate,
            Currency = string.IsNullOrWhiteSpace(currency) ? "HNL" : currency.Trim().ToUpperInvariant(),
            Status = InvoiceStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Agrega una línea copiando del cargo. Solo mientras la factura es borrador: una factura
    /// timbrada con una línea agregada después es un documento que no coincide con el número que
    /// la autoridad autorizó.
    /// </summary>
    public void AddLine(InvoiceLine line, TimeProvider? timeProvider = null)
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new InvalidOperationException(
                $"Una factura en estado '{Status}' ya no admite líneas.");
        }
        _lines.Add(line);
        Recalculate(timeProvider);
    }

    /// <summary>
    /// Suma las líneas. <b>El redondeo es solo al totalizar</b>: redondear línea por línea y
    /// después sumar produce centavos de diferencia contra el mismo cálculo hecho de una vez, y
    /// esa diferencia es la que no cuadra al cierre del día.
    /// </summary>
    private void Recalculate(TimeProvider? timeProvider = null)
    {
        Subtotal = Math.Round(_lines.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero);
        DiscountTotal = Math.Round(_lines.Sum(l => l.DiscountAmount), 2, MidpointRounding.AwayFromZero);
        TaxTotal = Math.Round(_lines.Sum(l => l.TaxAmount), 2, MidpointRounding.AwayFromZero);
        GrandTotal = Math.Round(Subtotal - DiscountTotal + TaxTotal, 2, MidpointRounding.AwayFromZero);
        Balance = GrandTotal;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Subtotal neto de las líneas con un tratamiento dado. Alimenta el documento fiscal.</summary>
    public decimal SubtotalFor(TaxTreatment treatment, decimal? rate = null) =>
        Math.Round(
            _lines
                .Where(l => l.TaxTreatment == treatment && (rate is null || l.TaxRate == rate))
                .Sum(l => l.NetAmount),
            2, MidpointRounding.AwayFromZero);

    /// <summary>Marca la factura como timbrada y la deja inmutable.</summary>
    public void MarkIssued(Guid fiscalDocumentId, TimeProvider? timeProvider = null)
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new InvalidOperationException($"La factura ya está en estado '{Status}'.");
        }
        if (_lines.Count == 0)
        {
            throw new InvalidOperationException("Una factura sin líneas no se emite.");
        }
        FiscalDocumentId = fiscalDocumentId;
        Status = InvoiceStatus.Issued;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Void(TimeProvider? timeProvider = null)
    {
        if (Status == InvoiceStatus.Voided)
        {
            return;
        }
        if (Status == InvoiceStatus.Paid)
        {
            throw new InvalidOperationException(
                "Una factura pagada no se anula: se emite una nota de crédito y se devuelve el dinero.");
        }
        Status = InvoiceStatus.Voided;
        Balance = 0m;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}

/// <summary>
/// Una línea de la factura, copiada del cargo.
///
/// <para><b>Todo es snapshot.</b> Se copia del cargo —que a su vez congeló el catálogo— para que
/// la factura sobreviva al borrado del ítem, al cambio de tasa y a la reforma tributaria. Una
/// factura que necesita consultar el catálogo para reimprimirse no se puede reimprimir.</para>
/// </summary>
public sealed class InvoiceLine
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid InvoiceId { get; private set; }

    /// <summary>Cargo del que salió. Sirve para volver al acto clínico desde la factura.</summary>
    public Guid ChargeId { get; private set; }

    public string DescriptionSnapshot { get; private set; } = string.Empty;
    public string TaxCodeSnapshot { get; private set; } = string.Empty;
    public TaxTreatment TaxTreatment { get; private set; }
    public decimal TaxRate { get; private set; }

    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal DiscountAmount { get; private set; }
    public int LineNumber { get; private set; }

    private InvoiceLine() { }

    public static InvoiceLine FromCharge(
        Guid organizationId,
        Guid invoiceId,
        Guid chargeId,
        int lineNumber,
        string descriptionSnapshot,
        string taxCodeSnapshot,
        TaxTreatment taxTreatment,
        decimal taxRate,
        decimal quantity,
        decimal unitPrice,
        decimal discountAmount,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new InvoiceLine
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            InvoiceId = invoiceId,
            ChargeId = chargeId,
            LineNumber = lineNumber,
            DescriptionSnapshot = descriptionSnapshot,
            TaxCodeSnapshot = taxCodeSnapshot,
            TaxTreatment = taxTreatment,
            TaxRate = taxRate,
            Quantity = quantity,
            UnitPrice = unitPrice,
            DiscountAmount = discountAmount,
        };
    }

    public decimal NetAmount => Math.Round(Quantity * UnitPrice - DiscountAmount, 2, MidpointRounding.AwayFromZero);
    public decimal TaxAmount => Math.Round(NetAmount * TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
    public decimal Total => NetAmount + TaxAmount;
}
