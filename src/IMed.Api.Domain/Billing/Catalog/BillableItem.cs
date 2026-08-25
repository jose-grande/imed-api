namespace IMed.Api.Domain.Billing.Catalog;

/// <summary>
/// El catálogo maestro de cosas cobrables — el <i>Charge Description Master</i> (FSF bloque 2).
///
/// <para><b>Por qué uno solo.</b> Es lo que usan todos los sistemas hospitalarios serios, y es
/// universal por una razón: sin catálogo único, cada punto de captura inventa su propio precio.
/// El laboratorio cobra el hemograma a un precio, recepción a otro, y la clínica pierde el
/// control de sus márgenes sin que nada avise.</para>
///
/// <para><b>Los enlaces al mundo real son excluyentes.</b> Un cobrable puede venir de una prueba
/// del LIS, de un panel, de un producto de inventario o de nada —un servicio suelto—, pero no de
/// dos cosas a la vez: si lo fuera, capturar el cargo tendría dos orígenes posibles y la
/// idempotencia de FAC-4 dejaría de poder distinguirlos.</para>
/// </summary>
public sealed class BillableItem
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Código interno del cargo, único por organización. Es por donde busca la caja.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public BillableItemKind Kind { get; private set; }
    public BillableComponent Component { get; private set; }

    /// <summary>
    /// Tributo aplicable. Obligatorio: sin él, la línea no sabe si paga ISV, y el default
    /// silencioso —"asumamos exento"— es cómo se declara de menos sin darse cuenta.
    /// </summary>
    public Guid TaxCodeId { get; private set; }

    /// <summary>
    /// Precio base. Es el último recurso de la resolución, cuando no hay tarifario que aplique.
    /// </summary>
    public decimal DefaultPrice { get; private set; }

    /// <summary>Cuándo se cobra (hueco G2). Lo consulta la puerta de prepago de FAC-7.</summary>
    public PaymentTiming PaymentTiming { get; private set; }

    // ── Enlaces al mundo real, todos nullable y excluyentes entre sí ──

    /// <summary>Prueba del catálogo del LIS que dispara este cargo.</summary>
    public Guid? LabTestId { get; private set; }

    /// <summary>Panel del catálogo del LIS que dispara este cargo.</summary>
    public Guid? LabPanelId { get; private set; }

    /// <summary>
    /// Ítem de inventario. <b>Declarado y sin uso hasta U2.</b> Cuando exista, facturar una línea
    /// con este enlace obliga a mover stock en la misma transacción — es la costura que evita el
    /// descuadre entre inventario y ventas.
    /// </summary>
    public Guid? StockItemId { get; private set; }

    /// <summary>Código de servicio (LOINC/SNOMED/CPT local) para lo que no tiene producto detrás.</summary>
    public string? ServiceCode { get; private set; }

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private BillableItem() { }

    public static BillableItem Create(
        Guid organizationId,
        string code,
        string name,
        BillableItemKind kind,
        Guid taxCodeId,
        decimal defaultPrice,
        PaymentTiming paymentTiming = PaymentTiming.Either,
        BillableComponent component = BillableComponent.Global,
        Guid? labTestId = null,
        Guid? labPanelId = null,
        Guid? stockItemId = null,
        string? serviceCode = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código del cobrable es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del cobrable es obligatorio.", nameof(name));
        }
        if (taxCodeId == Guid.Empty)
        {
            throw new ArgumentException(
                "El tributo es obligatorio: sin él la línea no sabe si paga ISV.", nameof(taxCodeId));
        }
        if (defaultPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultPrice), "El precio no puede ser negativo.");
        }

        var enlaces = new[] { labTestId.HasValue, labPanelId.HasValue, stockItemId.HasValue }.Count(x => x);
        if (enlaces > 1)
        {
            throw new ArgumentException(
                "Un cobrable enlaza a una prueba, a un panel o a un ítem de inventario, no a varios.",
                nameof(labTestId));
        }

        // Nota para U2: un producto sin `stockItemId` no se puede descontar de ninguna bodega y
        // termina vendiéndose sin bajar existencias. No se valida todavía porque el inventario no
        // existe y exigir un id que nadie puede dar bloquearía el catálogo entero.

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new BillableItem
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Kind = kind,
            Component = component,
            TaxCodeId = taxCodeId,
            DefaultPrice = defaultPrice,
            PaymentTiming = paymentTiming,
            LabTestId = labTestId,
            LabPanelId = labPanelId,
            StockItemId = stockItemId,
            ServiceCode = string.IsNullOrWhiteSpace(serviceCode) ? null : serviceCode.Trim(),
            Active = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void UpdateDetails(
        string name,
        Guid taxCodeId,
        decimal defaultPrice,
        PaymentTiming paymentTiming,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre del cobrable es obligatorio.", nameof(name));
        }
        if (taxCodeId == Guid.Empty)
        {
            throw new ArgumentException("El tributo es obligatorio.", nameof(taxCodeId));
        }
        if (defaultPrice < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultPrice), "El precio no puede ser negativo.");
        }

        Name = name.Trim();
        TaxCodeId = taxCodeId;
        DefaultPrice = defaultPrice;
        PaymentTiming = paymentTiming;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Enlaza este cobrable a una prueba o panel del LIS. Reemplaza el enlace anterior.</summary>
    public void LinkToLab(Guid? labTestId, Guid? labPanelId, TimeProvider? timeProvider = null)
    {
        if (labTestId.HasValue && labPanelId.HasValue)
        {
            throw new ArgumentException(
                "Un cobrable enlaza a una prueba o a un panel, no a los dos.", nameof(labTestId));
        }
        LabTestId = labTestId;
        LabPanelId = labPanelId;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Deactivate(TimeProvider? timeProvider = null)
    {
        Active = false;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void Reactivate(TimeProvider? timeProvider = null)
    {
        Active = true;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
