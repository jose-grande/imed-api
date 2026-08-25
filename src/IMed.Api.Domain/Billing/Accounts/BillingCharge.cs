using IMed.Api.Domain.Billing.Catalog;

namespace IMed.Api.Domain.Billing.Accounts;

/// <summary>
/// Un cargo en una cuenta — el corazón operativo de la facturación (FSF bloque 3).
///
/// <para><b>Todo lo que se imprime está congelado.</b> <see cref="UnitPrice"/>,
/// <see cref="PriceSource"/>, <see cref="TaxRate"/> y <see cref="DescriptionSnapshot"/> se copian
/// al capturar y no se vuelven a leer del catálogo. Subir el precio en el tarifario no puede
/// cambiar lo que ya se le cobró a alguien: la factura de ayer tiene que poder reimprimirse igual
/// dentro de cinco años, aunque el ítem ya no exista.</para>
///
/// <para><b>El origen es la clave de idempotencia.</b> <see cref="SourceKind"/> +
/// <see cref="SourceId"/> identifican el acto clínico que originó el cargo. El índice único sobre
/// esa combinación es lo que impide que un reproceso —un resultado que se vuelve a liberar, un
/// job que reintenta, un doble clic— le cobre dos veces al paciente. Es la clase de defecto que
/// no se ve en pruebas y aparece como un reclamo.</para>
/// </summary>
public sealed class BillingCharge
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid BillableItemId { get; private set; }

    public decimal Quantity { get; private set; }

    /// <summary>Precio unitario congelado al capturar.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>De qué escalón salió el precio. Es lo que contesta "¿por qué se le cobró esto?".</summary>
    public PriceSource PriceSource { get; private set; }

    /// <summary>Tarifario que ganó, si fue alguno.</summary>
    public Guid? PriceListId { get; private set; }

    public Guid TaxCodeId { get; private set; }

    /// <summary>Tasa congelada. Una reforma tributaria no puede recalcular lo ya cobrado.</summary>
    public decimal TaxRate { get; private set; }

    /// <summary>Descripción congelada: es lo que sale impreso en la línea de la factura.</summary>
    public string DescriptionSnapshot { get; private set; } = string.Empty;

    public decimal DiscountAmount { get; private set; }

    /// <summary>
    /// Quién autorizó el descuento, cuando pasó el tope de quien lo pidió. Null significa que el
    /// descuento estaba dentro del tope, no que nadie lo autorizó.
    /// </summary>
    public Guid? DiscountAuthorizedBy { get; private set; }

    public BillableComponent Component { get; private set; }
    public DateTimeOffset PerformedAt { get; private set; }
    public string? PerformerPractitionerId { get; private set; }

    public ChargeStatus Status { get; private set; }
    public ChargeSourceKind SourceKind { get; private set; }

    /// <summary>
    /// Identificador del acto que originó el cargo, dentro de <see cref="SourceKind"/>. Para un
    /// cargo manual es un id propio de esa captura: sin él, dos cargos manuales del mismo ítem
    /// chocarían contra el índice de idempotencia, y agregar dos veces algo a mano es legítimo.
    /// </summary>
    public string SourceId { get; private set; } = string.Empty;

    /// <summary>Referencia FHIR del origen, para poder volver al acto desde el estado de cuenta.</summary>
    public string? SourceFhirRef { get; private set; }

    /// <summary>Motivo, obligatorio en los cargos manuales — que son los que audita el dueño.</summary>
    public string? Reason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? ReversedAt { get; private set; }
    public string? ReversalReason { get; private set; }

    private BillingCharge() { }

    public static BillingCharge Capture(
        Guid organizationId,
        Guid accountId,
        Guid billableItemId,
        string descriptionSnapshot,
        decimal quantity,
        decimal unitPrice,
        PriceSource priceSource,
        Guid taxCodeId,
        decimal taxRate,
        ChargeSourceKind sourceKind,
        string sourceId,
        Guid? priceListId = null,
        decimal discountAmount = 0m,
        Guid? discountAuthorizedBy = null,
        BillableComponent component = BillableComponent.Global,
        DateTimeOffset? performedAt = null,
        string? performerPractitionerId = null,
        string? sourceFhirRef = null,
        string? reason = null,
        TimeProvider? timeProvider = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad tiene que ser positiva.");
        }
        if (unitPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "El precio no puede ser negativo.");
        }
        if (discountAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(discountAmount), "El descuento no puede ser negativo.");
        }
        if (string.IsNullOrWhiteSpace(descriptionSnapshot))
        {
            throw new ArgumentException(
                "La descripción es obligatoria: es lo que sale impreso.", nameof(descriptionSnapshot));
        }
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            throw new ArgumentException(
                "El cargo necesita saber de qué acto vino: sin origen no hay idempotencia.",
                nameof(sourceId));
        }

        var bruto = quantity * unitPrice;
        if (discountAmount > bruto)
        {
            throw new ArgumentException(
                "El descuento no puede superar el importe de la línea.", nameof(discountAmount));
        }

        // Un cargo tecleado a mano sin motivo es exactamente el que nadie puede explicar después,
        // y es el que sale en el reporte de excepciones que el dueño va a pedir el primer mes.
        if (sourceKind == ChargeSourceKind.Manual && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Un cargo manual exige motivo.", nameof(reason));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new BillingCharge
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            AccountId = accountId,
            BillableItemId = billableItemId,
            DescriptionSnapshot = descriptionSnapshot.Trim(),
            Quantity = quantity,
            UnitPrice = unitPrice,
            PriceSource = priceSource,
            PriceListId = priceListId,
            TaxCodeId = taxCodeId,
            TaxRate = taxRate,
            DiscountAmount = discountAmount,
            DiscountAuthorizedBy = discountAuthorizedBy,
            Component = component,
            PerformedAt = performedAt ?? now,
            PerformerPractitionerId = string.IsNullOrWhiteSpace(performerPractitionerId)
                ? null : performerPractitionerId.Trim(),
            Status = ChargeStatus.Captured,
            SourceKind = sourceKind,
            SourceId = sourceId.Trim(),
            SourceFhirRef = string.IsNullOrWhiteSpace(sourceFhirRef) ? null : sourceFhirRef.Trim(),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Importe de la línea antes de impuesto, ya con el descuento aplicado.</summary>
    public decimal NetAmount => Math.Round(Quantity * UnitPrice - DiscountAmount, 2, MidpointRounding.AwayFromZero);

    /// <summary>Impuesto de la línea, con la tasa congelada.</summary>
    public decimal TaxAmount => Math.Round(NetAmount * TaxRate / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>Lo que suma esta línea al total.</summary>
    public decimal Total => NetAmount + TaxAmount;

    /// <summary>
    /// Reversa el cargo. No se borra: el movimiento tiene que poder explicarse, y un cargo que
    /// desaparece sin rastro es indistinguible de uno que nunca se capturó.
    /// </summary>
    public void Reverse(string reason, TimeProvider? timeProvider = null)
    {
        if (Status == ChargeStatus.Invoiced)
        {
            throw new InvalidOperationException(
                "Un cargo ya facturado no se reversa: se anula la factura o se emite una nota de crédito.");
        }
        if (Status == ChargeStatus.Reversed)
        {
            return; // idempotente
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Reversar exige motivo.", nameof(reason));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        Status = ChargeStatus.Reversed;
        ReversedAt = now;
        ReversalReason = reason.Trim();
        UpdatedAt = now;
    }

    public void MarkInvoiced(TimeProvider? timeProvider = null)
    {
        if (Status == ChargeStatus.Reversed)
        {
            throw new InvalidOperationException("Un cargo reversado no se factura.");
        }
        Status = ChargeStatus.Invoiced;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
