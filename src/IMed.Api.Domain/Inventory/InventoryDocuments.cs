namespace IMed.Api.Domain.Inventory;

/// <summary>
/// 🔴 Entrada de mercadería: compra a un proveedor con su factura (D7, FSF §8 <c>goods_receipt</c>).
///
/// <para><b>Es el único lugar donde nacen lotes y vencimientos</b>, y por eso el control de calidad
/// de todo el módulo: un vencimiento mal capturado aquí hace mentir al FEFO y a las alertas para
/// siempre. Se valida en servidor, no en pantalla.</para>
///
/// <para><b>La factura del proveedor es su llave natural.</b> La misma factura del mismo proveedor
/// no entra dos veces: el índice único lo impide, y eso también vuelve idempotente un doble clic.</para>
///
/// <para>Se registra confirmada (mueve el libro al guardarse). Un error se corrige anulándola, que
/// saca lo que entró a su mismo costo, mientras esa existencia siga en la bodega.</para>
/// </summary>
public sealed class GoodsReceipt
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid SupplierId { get; private set; }
    public string SupplierInvoiceNumber { get; private set; } = string.Empty;
    public DateOnly SupplierInvoiceDate { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public Guid? ReceivedBy { get; private set; }
    public string? Notes { get; private set; }
    public decimal Total { get; private set; }
    public bool Voided { get; private set; }
    public DateTimeOffset? VoidedAt { get; private set; }
    public Guid? VoidedBy { get; private set; }
    public string? VoidReason { get; private set; }

    private readonly List<GoodsReceiptLine> _lines = [];
    public IReadOnlyList<GoodsReceiptLine> Lines => _lines;

    private GoodsReceipt() { }

    public static GoodsReceipt Create(
        Guid organizationId, Guid warehouseId, Guid supplierId, string supplierInvoiceNumber,
        DateOnly supplierInvoiceDate, Guid? receivedBy, string? notes, TimeProvider? timeProvider = null)
    {
        var numero = (supplierInvoiceNumber ?? string.Empty).Trim();
        if (numero.Length is 0 or > 60)
        {
            throw new ArgumentException(
                "El número de la factura del proveedor es obligatorio: es lo que respalda la compra.",
                nameof(supplierInvoiceNumber));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new GoodsReceipt
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            SupplierId = supplierId,
            SupplierInvoiceNumber = numero,
            SupplierInvoiceDate = supplierInvoiceDate,
            ReceivedAt = now,
            ReceivedBy = receivedBy,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
    }

    public GoodsReceiptLine AddLine(
        Guid stockItemId, Guid stockLotId, string? presentationName, decimal presentationQuantity,
        decimal factorToBase, decimal unitCostPerPresentation)
    {
        var linea = GoodsReceiptLine.Create(
            Id, _lines.Count + 1, stockItemId, stockLotId, presentationName, presentationQuantity,
            factorToBase, unitCostPerPresentation);
        _lines.Add(linea);
        Total = _lines.Sum(l => l.LineTotal);
        return linea;
    }

    public void Void(Guid? by, string reason, TimeProvider? timeProvider = null)
    {
        if (Voided)
        {
            throw new InvalidOperationException("La entrada ya está anulada.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Anular una entrada exige motivo.", nameof(reason));
        }
        Voided = true;
        VoidedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        VoidedBy = by;
        VoidReason = reason.Trim();
    }
}

public sealed class GoodsReceiptLine
{
    public Guid Id { get; private set; }
    public Guid GoodsReceiptId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid StockItemId { get; private set; }
    public Guid StockLotId { get; private set; }

    /// <summary>Presentación en la que se capturó ("Caja x 30"), o null si fue en unidad base.</summary>
    public string? PresentationName { get; private set; }

    public decimal PresentationQuantity { get; private set; }
    public decimal FactorToBase { get; private set; }

    /// <summary>Cantidad en unidad base: lo que entra al libro.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Costo por unidad base, sin impuesto. Es lo que alimenta el promedio (D13).</summary>
    public decimal UnitCost { get; private set; }

    public decimal LineTotal { get; private set; }

    private GoodsReceiptLine() { }

    internal static GoodsReceiptLine Create(
        Guid receiptId, int lineNumber, Guid stockItemId, Guid stockLotId, string? presentationName,
        decimal presentationQuantity, decimal factorToBase, decimal unitCostPerPresentation)
    {
        if (presentationQuantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(presentationQuantity), "La cantidad recibida tiene que ser positiva.");
        }
        if (factorToBase <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factorToBase), "La presentación tiene que traer al menos una unidad.");
        }
        if (unitCostPerPresentation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCostPerPresentation), "El costo no puede ser negativo.");
        }
        var cantidad = presentationQuantity * factorToBase;
        return new GoodsReceiptLine
        {
            Id = Guid.CreateVersion7(),
            GoodsReceiptId = receiptId,
            LineNumber = lineNumber,
            StockItemId = stockItemId,
            StockLotId = stockLotId,
            PresentationName = string.IsNullOrWhiteSpace(presentationName) ? null : presentationName.Trim(),
            PresentationQuantity = presentationQuantity,
            FactorToBase = factorToBase,
            Quantity = cantidad,
            UnitCost = Math.Round(unitCostPerPresentation / factorToBase, 6, MidpointRounding.AwayFromZero),
            LineTotal = Math.Round(presentationQuantity * unitCostPerPresentation, 4, MidpointRounding.AwayFromZero),
        };
    }
}

/// <summary>
/// Traslado entre bodegas (FSF §8 <c>stock_transfer</c>). En el libro son dos filas con el mismo
/// grupo —sale de una, entra a la otra— al mismo costo: la partida doble que hace auditable el
/// libro. Se confirma al guardarse.
/// </summary>
public sealed class StockTransfer
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid FromWarehouseId { get; private set; }
    public Guid ToWarehouseId { get; private set; }
    public DateTimeOffset TransferredAt { get; private set; }
    public Guid? TransferredBy { get; private set; }
    public string? Notes { get; private set; }

    private readonly List<StockDocumentLine> _lines = [];
    public IReadOnlyList<StockDocumentLine> Lines => _lines;

    private StockTransfer() { }

    public static StockTransfer Create(
        Guid organizationId, Guid fromWarehouseId, Guid toWarehouseId, Guid? by, string? notes,
        TimeProvider? timeProvider = null)
    {
        if (fromWarehouseId == toWarehouseId)
        {
            throw new ArgumentException("El origen y el destino del traslado son la misma bodega.", nameof(toWarehouseId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new StockTransfer
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            FromWarehouseId = fromWarehouseId,
            ToWarehouseId = toWarehouseId,
            TransferredAt = now,
            TransferredBy = by,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
    }

    public StockDocumentLine AddLine(Guid stockItemId, Guid stockLotId, decimal quantity)
    {
        var l = StockDocumentLine.Create(Id, _lines.Count + 1, stockItemId, stockLotId, 1, quantity);
        _lines.Add(l);
        return l;
    }
}

/// <summary>
/// Consumo interno manual (D9): lo que una área gasta sin cobrarlo a nadie (gasas en curaciones,
/// reactivos de un control). Sin descuento automático por examen: lo registra quien lo consume.
/// </summary>
public sealed class InternalConsumption
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }

    /// <summary>Área o servicio que consumió ("Sala de procedimientos", "Laboratorio — control").</summary>
    public string Area { get; private set; } = string.Empty;

    public string? Notes { get; private set; }
    public DateTimeOffset ConsumedAt { get; private set; }
    public Guid? ConsumedBy { get; private set; }

    private readonly List<StockDocumentLine> _lines = [];
    public IReadOnlyList<StockDocumentLine> Lines => _lines;

    private InternalConsumption() { }

    public static InternalConsumption Create(
        Guid organizationId, Guid warehouseId, string area, string? notes, Guid? by, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(area))
        {
            throw new ArgumentException("Indicá qué área consumió: sin eso el reporte de consumo no dice nada.", nameof(area));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new InternalConsumption
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            Area = area.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            ConsumedAt = now,
            ConsumedBy = by,
        };
    }

    public StockDocumentLine AddLine(Guid stockItemId, Guid stockLotId, decimal quantity)
    {
        var l = StockDocumentLine.Create(Id, _lines.Count + 1, stockItemId, stockLotId, -1, quantity);
        _lines.Add(l);
        return l;
    }
}

/// <summary>
/// Ajuste de inventario con motivo de una lista (P8). Si su valor a costo supera el monto de
/// aprobación, espera al gerente de inventario antes de tocar el libro: un ajuste es la forma más
/// fácil de esconder un faltante, y por eso el grande lo firma alguien más.
/// </summary>
public sealed class StockAdjustment
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public AdjustmentReason Reason { get; private set; }
    public string? Notes { get; private set; }
    public StockDocumentStatus Status { get; private set; }

    /// <summary>Valor absoluto a costo de todas las líneas al pedirse: lo que se compara con el umbral.</summary>
    public decimal Value { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }
    public Guid? RequestedBy { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public string? RejectionReason { get; private set; }

    /// <summary>Conteo físico que lo originó, si vino de uno.</summary>
    public Guid? StockCountId { get; private set; }

    private readonly List<StockDocumentLine> _lines = [];
    public IReadOnlyList<StockDocumentLine> Lines => _lines;

    private StockAdjustment() { }

    public static StockAdjustment Create(
        Guid organizationId, Guid warehouseId, AdjustmentReason reason, string? notes, Guid? requestedBy,
        Guid? stockCountId = null, TimeProvider? timeProvider = null)
    {
        if (reason is AdjustmentReason.Other && string.IsNullOrWhiteSpace(notes))
        {
            throw new ArgumentException("Con el motivo \"Otro\" hay que explicar el ajuste.", nameof(notes));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new StockAdjustment
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            Reason = reason,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            Status = StockDocumentStatus.Draft,
            RequestedAt = now,
            RequestedBy = requestedBy,
            StockCountId = stockCountId,
        };
    }

    public StockDocumentLine AddLine(Guid stockItemId, Guid stockLotId, int direction, decimal quantity, decimal unitCost)
    {
        if (Status != StockDocumentStatus.Draft)
        {
            throw new InvalidOperationException("El ajuste ya no se puede modificar.");
        }
        var l = StockDocumentLine.Create(Id, _lines.Count + 1, stockItemId, stockLotId, direction, quantity);
        l.SetUnitCost(unitCost);
        _lines.Add(l);
        Value = Math.Round(_lines.Sum(x => x.Quantity * x.UnitCost), 4, MidpointRounding.AwayFromZero);
        return l;
    }

    /// <summary>
    /// Decide si el ajuste se aplica ya o espera aprobación. Quien tiene el permiso de aprobar no
    /// necesita pedírselo a sí mismo; los demás, por encima del umbral, sí.
    /// </summary>
    public bool Submit(decimal threshold, bool requesterCanApprove, TimeProvider? timeProvider = null)
    {
        if (Status != StockDocumentStatus.Draft)
        {
            throw new InvalidOperationException("El ajuste ya fue enviado.");
        }
        if (_lines.Count == 0)
        {
            throw new ArgumentException("El ajuste no tiene líneas.");
        }
        if (Value > threshold && !requesterCanApprove)
        {
            Status = StockDocumentStatus.PendingApproval;
            return false;
        }
        Status = StockDocumentStatus.Posted;
        DecidedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DecidedBy = RequestedBy;
        return true;
    }

    public void Approve(Guid? by, TimeProvider? timeProvider = null)
    {
        if (Status != StockDocumentStatus.PendingApproval)
        {
            throw new InvalidOperationException("Solo se aprueba un ajuste que está esperando aprobación.");
        }
        Status = StockDocumentStatus.Posted;
        DecidedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DecidedBy = by;
    }

    public void Reject(Guid? by, string reason, TimeProvider? timeProvider = null)
    {
        if (Status != StockDocumentStatus.PendingApproval)
        {
            throw new InvalidOperationException("Solo se rechaza un ajuste que está esperando aprobación.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Rechazar un ajuste exige motivo.", nameof(reason));
        }
        Status = StockDocumentStatus.Rejected;
        DecidedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        DecidedBy = by;
        RejectionReason = reason.Trim();
    }
}

/// <summary>
/// Línea de traslado, consumo o ajuste: artículo, lote, sentido y cantidad en unidad base. El
/// costo se llena al aplicarse (el promedio vigente), salvo en un ajuste que suma, que lo trae.
/// </summary>
public sealed class StockDocumentLine
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid StockItemId { get; private set; }
    public Guid StockLotId { get; private set; }
    public int Direction { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal UnitCost { get; private set; }

    private StockDocumentLine() { }

    internal static StockDocumentLine Create(
        Guid documentId, int lineNumber, Guid stockItemId, Guid stockLotId, int direction, decimal quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad tiene que ser positiva.");
        }
        if (direction is not (1 or -1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction));
        }
        return new StockDocumentLine
        {
            Id = Guid.CreateVersion7(),
            DocumentId = documentId,
            LineNumber = lineNumber,
            StockItemId = stockItemId,
            StockLotId = stockLotId,
            Direction = direction,
            Quantity = quantity,
        };
    }

    public void SetUnitCost(decimal unitCost)
    {
        if (unitCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCost), "El costo no puede ser negativo.");
        }
        UnitCost = Math.Round(unitCost, 6, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// Conteo físico de una bodega (FSF §8 <c>stock_count</c>). Se abre con la existencia del sistema
/// por lote, se captura lo contado y, al cerrarlo, la diferencia se convierte en un ajuste con
/// motivo "diferencia de conteo" ligado al conteo, que pasa por la misma regla de aprobación.
///
/// <para>La diferencia se calcula contra la existencia <b>al cerrar</b>, no al abrir: si mientras se
/// contaba hubo un despacho, compararlo con la foto vieja inventaría un faltante.</para>
/// </summary>
public sealed class StockCount
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public StockDocumentStatus Status { get; private set; }
    public string? Notes { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public Guid? StartedBy { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid? ClosedBy { get; private set; }

    /// <summary>Ajuste generado al cerrar, si hubo diferencias.</summary>
    public Guid? AdjustmentId { get; private set; }

    private readonly List<StockCountLine> _lines = [];
    public IReadOnlyList<StockCountLine> Lines => _lines;

    private StockCount() { }

    public static StockCount Start(
        Guid organizationId, Guid warehouseId, string? notes, Guid? by, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new StockCount
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            WarehouseId = warehouseId,
            Status = StockDocumentStatus.Draft,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            StartedAt = now,
            StartedBy = by,
        };
    }

    public StockCountLine AddLine(Guid stockItemId, Guid stockLotId, decimal expectedQuantity)
    {
        EnsureDraft();
        var l = StockCountLine.Create(Id, stockItemId, stockLotId, expectedQuantity);
        _lines.Add(l);
        return l;
    }

    public void Record(Guid lineId, decimal? countedQuantity)
    {
        EnsureDraft();
        var linea = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new ArgumentException("La línea no es de este conteo.", nameof(lineId));
        linea.Record(countedQuantity);
    }

    public void Close(Guid? by, Guid? adjustmentId, TimeProvider? timeProvider = null)
    {
        EnsureDraft();
        if (_lines.Any(l => l.CountedQuantity is null))
        {
            throw new InvalidOperationException(
                "Faltan líneas por contar. Si un lote no está en el estante, contalo en cero.");
        }
        Status = StockDocumentStatus.Posted;
        ClosedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ClosedBy = by;
        AdjustmentId = adjustmentId;
    }

    public void Cancel(Guid? by, TimeProvider? timeProvider = null)
    {
        EnsureDraft();
        Status = StockDocumentStatus.Rejected;
        ClosedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ClosedBy = by;
    }

    private void EnsureDraft()
    {
        if (Status != StockDocumentStatus.Draft)
        {
            throw new InvalidOperationException("El conteo ya está cerrado.");
        }
    }
}

public sealed class StockCountLine
{
    public Guid Id { get; private set; }
    public Guid StockCountId { get; private set; }
    public Guid StockItemId { get; private set; }
    public Guid StockLotId { get; private set; }

    /// <summary>Existencia del sistema al abrir el conteo. Referencia; la diferencia se toma al cerrar.</summary>
    public decimal ExpectedQuantity { get; private set; }

    public decimal? CountedQuantity { get; private set; }

    private StockCountLine() { }

    internal static StockCountLine Create(Guid countId, Guid stockItemId, Guid stockLotId, decimal expected) => new()
    {
        Id = Guid.CreateVersion7(),
        StockCountId = countId,
        StockItemId = stockItemId,
        StockLotId = stockLotId,
        ExpectedQuantity = expected,
    };

    internal void Record(decimal? counted)
    {
        if (counted is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counted), "Lo contado no puede ser negativo.");
        }
        CountedQuantity = counted;
    }
}
