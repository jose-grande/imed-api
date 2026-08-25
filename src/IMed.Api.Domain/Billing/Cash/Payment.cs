namespace IMed.Api.Domain.Billing.Cash;

/// <summary>
/// Un cobro (FSF §7.2).
///
/// <para><b>Un pago no pertenece a una factura.</b> Se aplica a una o varias mediante
/// <see cref="PaymentAllocation"/>. Modelarlo 1:1 parece más simple y se paga caro al primer
/// abono: el paciente que deja L 500 a cuenta de dos facturas, o el que paga tres consultas de la
/// familia con una tarjeta, no entran en ese modelo — y rehacerlo después obliga a migrar pagos
/// ya cobrados.</para>
/// </summary>
public sealed class Payment
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PartyId { get; private set; }
    public PaymentMethod Method { get; private set; }

    /// <summary>Lo que el pagador entregó. En efectivo puede ser más que lo aplicado: el resto es vuelto.</summary>
    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "HNL";

    /// <summary>Número de voucher, de transferencia o de cheque. Es lo que permite conciliar.</summary>
    public string? Reference { get; private set; }

    /// <summary>Turno en el que se cobró. Es lo que hace que el pago cuente en ese arqueo.</summary>
    public Guid CashSessionId { get; private set; }

    public Guid ReceivedBy { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public PaymentStatus Status { get; private set; }
    public string? ReversalReason { get; private set; }

    private readonly List<PaymentAllocation> _allocations = [];
    public IReadOnlyList<PaymentAllocation> Allocations => _allocations;

    private Payment() { }

    public static Payment Receive(
        Guid organizationId,
        Guid partyId,
        Guid cashSessionId,
        PaymentMethod method,
        decimal amount,
        Guid receivedBy,
        string? reference = null,
        string currency = "HNL",
        TimeProvider? timeProvider = null)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(amount), "El pago tiene que ser positivo.");
        }

        // Sin referencia, una tarjeta o una transferencia no se puede conciliar contra el estado
        // de cuenta del banco, y el faltante aparece a fin de mes sin forma de rastrearlo.
        if (method is PaymentMethod.Card or PaymentMethod.Transfer or PaymentMethod.Check
            && string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException(
                $"Un pago con {method} exige referencia: sin ella no se puede conciliar.",
                nameof(reference));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new Payment
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            PartyId = partyId,
            CashSessionId = cashSessionId,
            Method = method,
            Amount = amount,
            Currency = string.IsNullOrWhiteSpace(currency) ? "HNL" : currency.Trim().ToUpperInvariant(),
            Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(),
            ReceivedBy = receivedBy,
            ReceivedAt = now,
            Status = PaymentStatus.Applied,
        };
    }

    public void Allocate(PaymentAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        var yaAplicado = _allocations.Sum(a => a.Amount);
        if (yaAplicado + allocation.Amount > Amount)
        {
            throw new InvalidOperationException(
                "No se puede aplicar más de lo que el pagador entregó.");
        }
        _allocations.Add(allocation);
    }

    /// <summary>Lo aplicado a facturas.</summary>
    public decimal AllocatedAmount => _allocations.Sum(a => a.Amount);

    /// <summary>
    /// Lo que sobra del pago. En efectivo es el vuelto; en tarjeta debería ser cero —no se
    /// devuelve efectivo de una tarjeta— y quien cobra tiene que verlo antes de confirmar.
    /// </summary>
    public decimal ChangeDue => Math.Round(Amount - AllocatedAmount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Lo que este pago aporta al efectivo de la caja. Solo el efectivo cuenta, y neto de vuelto:
    /// si el paciente pagó 500 por una consulta de 380, en la gaveta quedan 380.
    /// </summary>
    public decimal CashImpact =>
        Method == PaymentMethod.Cash && Status == PaymentStatus.Applied
            ? AllocatedAmount
            : 0m;

    public void Reverse(string reason, TimeProvider? timeProvider = null)
    {
        if (Status == PaymentStatus.Reversed)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Revertir un cobro exige motivo.", nameof(reason));
        }
        Status = PaymentStatus.Reversed;
        ReversalReason = reason.Trim();
    }
}

/// <summary>Cuánto de un pago se aplica a una factura.</summary>
public sealed class PaymentAllocation
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid PaymentId { get; private set; }
    public Guid InvoiceId { get; private set; }
    public decimal Amount { get; private set; }

    private PaymentAllocation() { }

    public static PaymentAllocation Create(
        Guid organizationId,
        Guid paymentId,
        Guid invoiceId,
        decimal amount,
        TimeProvider? timeProvider = null)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "La aplicación tiene que ser positiva.");
        }

        return new PaymentAllocation
        {
            Id = Guid.CreateVersion7((timeProvider ?? TimeProvider.System).GetUtcNow()),
            OrganizationId = organizationId,
            PaymentId = paymentId,
            InvoiceId = invoiceId,
            Amount = amount,
        };
    }
}
