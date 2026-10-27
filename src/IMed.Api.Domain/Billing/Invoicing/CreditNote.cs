using IMed.Api.Domain.Billing.Fiscal;

namespace IMed.Api.Domain.Billing.Invoicing;

/// <summary>
/// Nota de crédito (plan de salida a producción, D10 y A9): revierte total o parcialmente una
/// factura emitida con otro documento fiscal, con su propio número y su CAI.
///
/// <para><b>No edita la factura.</b> La factura timbrada queda como está; la nota de crédito es el
/// rastro de que se revirtió, por cuánto y por qué. Es lo que se usa fuera de la ventana de
/// anulación o cuando la factura ya se cobró.</para>
///
/// <para><b>Cada línea copia la de la factura</b> —precio, descuento prorrateado, tasa— para que el
/// ISV revertido sea exactamente el que se cobró, y apunta a ella para no poder acreditar dos veces
/// la misma cantidad.</para>
/// </summary>
public sealed class CreditNote
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid LocationId { get; private set; }
    public Guid? LegalEntityId { get; private set; }
    public Guid? FiscalDocumentId { get; private set; }

    public string Reason { get; private set; } = string.Empty;
    public Guid IssuedBy { get; private set; }
    public DateOnly IssueDate { get; private set; }

    public decimal Subtotal { get; private set; }
    public decimal DiscountTotal { get; private set; }
    public decimal TaxTotal { get; private set; }
    public decimal GrandTotal { get; private set; }

    /// <summary>Lo que redujo del saldo pendiente de la factura.</summary>
    public decimal AppliedToBalance { get; private set; }

    /// <summary>
    /// Lo que la clínica le debe devolver al paciente: la parte de la nota que excede lo que la
    /// factura tenía pendiente (ya estaba cobrada). Se paga desde una caja.
    /// </summary>
    public decimal RefundDue { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }
    public Guid? RefundCashSessionId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private readonly List<CreditNoteLine> _lines = [];
    public IReadOnlyList<CreditNoteLine> Lines => _lines;

    private CreditNote() { }

    public static CreditNote Draft(
        Invoice invoice, string reason, Guid issuedBy, DateOnly issueDate, TimeProvider? timeProvider = null)
    {
        if (invoice.Status is InvoiceStatus.Draft or InvoiceStatus.Voided)
        {
            throw new InvalidOperationException(
                $"Una factura en estado '{invoice.Status}' no admite nota de crédito.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("La nota de crédito exige motivo.", nameof(reason));
        }
        if (invoice.LocationId is not { } sede)
        {
            throw new InvalidOperationException("La factura no tiene sede emisora.");
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CreditNote
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = invoice.OrganizationId,
            InvoiceId = invoice.Id,
            AccountId = invoice.AccountId,
            LocationId = sede,
            LegalEntityId = invoice.LegalEntityId,
            Reason = reason.Trim(),
            IssuedBy = issuedBy,
            IssueDate = issueDate,
            CreatedAt = now,
        };
    }

    /// <summary>
    /// Acredita <paramref name="quantity"/> unidades de una línea de la factura. El descuento se
    /// prorratea por cantidad: acreditar la mitad devuelve la mitad del descuento.
    /// </summary>
    public void AddLine(InvoiceLine source, decimal quantity, decimal alreadyCredited)
    {
        if (FiscalDocumentId is not null)
        {
            throw new InvalidOperationException("La nota de crédito ya se emitió.");
        }
        if (source.InvoiceId != InvoiceId)
        {
            throw new ArgumentException("La línea no es de la factura de esta nota.", nameof(source));
        }
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad a acreditar tiene que ser positiva.");
        }
        if (_lines.Any(l => l.InvoiceLineId == source.Id))
        {
            throw new ArgumentException("La misma línea viene dos veces.", nameof(source));
        }
        var disponible = source.Quantity - alreadyCredited;
        if (quantity > disponible)
        {
            throw new InvalidOperationException(
                $"De «{source.DescriptionSnapshot}» quedan {disponible:0.##} por acreditar; se pidieron {quantity:0.##}.");
        }

        var descuento = source.Quantity == 0 ? 0m
            : Math.Round(source.DiscountAmount * quantity / source.Quantity, 2, MidpointRounding.AwayFromZero);
        _lines.Add(new CreditNoteLine(
            Guid.CreateVersion7(), OrganizationId, Id, source.Id, source.ChargeId, _lines.Count + 1,
            source.DescriptionSnapshot, source.TaxCodeSnapshot, source.TaxTreatment, source.TaxRate,
            quantity, source.UnitPrice, descuento));
        Recalculate();
    }

    private void Recalculate()
    {
        Subtotal = Math.Round(_lines.Sum(l => l.Quantity * l.UnitPrice), 2, MidpointRounding.AwayFromZero);
        DiscountTotal = Math.Round(_lines.Sum(l => l.DiscountAmount), 2, MidpointRounding.AwayFromZero);
        TaxTotal = Math.Round(_lines.Sum(l => l.TaxAmount), 2, MidpointRounding.AwayFromZero);
        GrandTotal = Math.Round(Subtotal - DiscountTotal + TaxTotal, 2, MidpointRounding.AwayFromZero);
    }

    public decimal SubtotalFor(TaxTreatment treatment, decimal? rate = null) =>
        Math.Round(
            _lines.Where(l => l.TaxTreatment == treatment && (rate is null || l.TaxRate == rate)).Sum(l => l.NetAmount),
            2, MidpointRounding.AwayFromZero);

    /// <summary>Queda emitida: la parte que cabía en el saldo lo baja; el resto es devolución.</summary>
    public void MarkIssued(Guid fiscalDocumentId, decimal appliedToBalance)
    {
        if (_lines.Count == 0)
        {
            throw new InvalidOperationException("Una nota de crédito sin líneas no se emite.");
        }
        FiscalDocumentId = fiscalDocumentId;
        AppliedToBalance = appliedToBalance;
        RefundDue = Math.Round(GrandTotal - appliedToBalance, 2, MidpointRounding.AwayFromZero);
    }

    public void MarkRefunded(Guid cashSessionId, TimeProvider? timeProvider = null)
    {
        if (RefundDue <= 0)
        {
            throw new InvalidOperationException("Esta nota de crédito no tiene devolución pendiente.");
        }
        if (RefundedAt is not null)
        {
            throw new InvalidOperationException("La devolución ya se registró.");
        }
        RefundedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        RefundCashSessionId = cashSessionId;
    }
}

/// <summary>Una línea de nota de crédito: copia de la línea de factura, por la cantidad acreditada.</summary>
public sealed class CreditNoteLine
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CreditNoteId { get; private set; }
    public Guid InvoiceLineId { get; private set; }
    public Guid ChargeId { get; private set; }
    public int LineNumber { get; private set; }
    public string DescriptionSnapshot { get; private set; } = string.Empty;
    public string TaxCodeSnapshot { get; private set; } = string.Empty;
    public TaxTreatment TaxTreatment { get; private set; }
    public decimal TaxRate { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal DiscountAmount { get; private set; }

    private CreditNoteLine() { }

    internal CreditNoteLine(
        Guid id, Guid organizationId, Guid creditNoteId, Guid invoiceLineId, Guid chargeId, int lineNumber,
        string description, string taxCode, TaxTreatment treatment, decimal taxRate,
        decimal quantity, decimal unitPrice, decimal discountAmount)
    {
        Id = id;
        OrganizationId = organizationId;
        CreditNoteId = creditNoteId;
        InvoiceLineId = invoiceLineId;
        ChargeId = chargeId;
        LineNumber = lineNumber;
        DescriptionSnapshot = description;
        TaxCodeSnapshot = taxCode;
        TaxTreatment = treatment;
        TaxRate = taxRate;
        Quantity = quantity;
        UnitPrice = unitPrice;
        DiscountAmount = discountAmount;
    }

    public decimal NetAmount => Math.Round(Quantity * UnitPrice - DiscountAmount, 2, MidpointRounding.AwayFromZero);
    public decimal TaxAmount => Math.Round(NetAmount * TaxRate / 100m, 2, MidpointRounding.AwayFromZero);
    public decimal Total => NetAmount + TaxAmount;
}
