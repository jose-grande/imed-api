namespace IMed.Api.Domain.Inventory;

/// <summary>
/// Lote de un artículo (FSF §8, D8). Nace en una entrada de mercadería —el único lugar donde se
/// captura un vencimiento— o en un ajuste de sobrante.
///
/// <para><b>Los artículos sin lote también tienen uno.</b> Un insumo que no exige lote usa un lote
/// técnico sin código de fabricante (<see cref="IsUntracked"/>). Así el saldo siempre es por
/// (bodega, lote) y el libro no tiene dos formas de guardar lo mismo.</para>
/// </summary>
public sealed class StockLot
{
    /// <summary>Código del lote técnico de los artículos que no exigen lote.</summary>
    public const string UntrackedCode = "SIN-LOTE";

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid StockItemId { get; private set; }

    /// <summary>Código del lote como lo imprime el fabricante.</summary>
    public string LotCode { get; private set; } = string.Empty;

    /// <summary>Vencimiento. Obligatorio en los lotes reales de medicamentos y reactivos.</summary>
    public DateOnly? ExpiryDate { get; private set; }

    public StockLotStatus Status { get; private set; }

    /// <summary>Lote técnico de un artículo que no exige lote.</summary>
    public bool IsUntracked { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private StockLot() { }

    public static StockLot Create(
        Guid organizationId, Guid stockItemId, string lotCode, DateOnly? expiryDate,
        TimeProvider? timeProvider = null)
    {
        var code = (lotCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is 0 or > 60)
        {
            throw new ArgumentException("El código del lote es obligatorio (hasta 60 caracteres).", nameof(lotCode));
        }
        if (code == UntrackedCode)
        {
            throw new ArgumentException($"'{UntrackedCode}' está reservado para los artículos sin lote.", nameof(lotCode));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new StockLot
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            StockItemId = stockItemId,
            LotCode = code,
            ExpiryDate = expiryDate,
            Status = StockLotStatus.Available,
            CreatedAt = now,
        };
    }

    public static StockLot CreateUntracked(Guid organizationId, Guid stockItemId, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new StockLot
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            StockItemId = stockItemId,
            LotCode = UntrackedCode,
            Status = StockLotStatus.Available,
            IsUntracked = true,
            CreatedAt = now,
        };
    }

    /// <summary>¿Está vencido el día indicado? Un lote vence al terminar su día de vencimiento.</summary>
    public bool IsExpiredOn(DateOnly day) => ExpiryDate is { } v && v < day;

    public void SetStatus(StockLotStatus status) => Status = status;
}

/// <summary>
/// 🔴 Una línea del libro de movimientos: <b>append-only</b>. Nada se edita ni se borra (FSF §8).
///
/// <para>El saldo materializado es caché; <b>el libro es la verdad</b>. Un error se corrige con
/// otro movimiento (un ajuste o una anulación que apunta al original con
/// <see cref="ReversalOfMovementId"/>), nunca tocando esta fila. La base lo hace cumplir con un
/// disparador que rechaza UPDATE y DELETE.</para>
///
/// <para><b>Cada fila lleva su consecuencia.</b> <see cref="QuantityAfter"/> y
/// <see cref="AverageCostAfter"/> son la existencia y el costo promedio del artículo en la bodega
/// después del movimiento. Así el kardex se lee de corrido, sin recalcular, y una diferencia entre
/// el libro y el saldo se nota fila por fila.</para>
/// </summary>
public sealed class StockMovement
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Agrupa las filas de un mismo acto (las dos patas de un traslado, las líneas de una entrada).</summary>
    public Guid GroupId { get; private set; }

    public StockMovementType Type { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid StockItemId { get; private set; }
    public Guid StockLotId { get; private set; }
    public Guid WarehouseId { get; private set; }

    /// <summary>+1 entra, −1 sale.</summary>
    public int Direction { get; private set; }

    /// <summary>Cantidad en unidad base, siempre positiva. El signo lo da <see cref="Direction"/>.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Costo unitario con el que se movió (el de compra al entrar; el promedio al salir).</summary>
    public decimal UnitCost { get; private set; }

    public decimal TotalCost { get; private set; }

    /// <summary>Existencia del artículo en la bodega (todos sus lotes) después del movimiento.</summary>
    public decimal QuantityAfter { get; private set; }

    /// <summary>Costo promedio del artículo en la bodega después del movimiento (D13).</summary>
    public decimal AverageCostAfter { get; private set; }

    /// <summary>Documento de origen: <c>goods_receipt</c>, <c>stock_transfer</c>, <c>pharmacy_dispense</c>…</summary>
    public string SourceKind { get; private set; } = string.Empty;

    public Guid SourceId { get; private set; }

    /// <summary>Línea del documento de origen, cuando el documento tiene varias.</summary>
    public Guid? SourceLineId { get; private set; }

    public Guid? PerformedBy { get; private set; }
    public Guid? ReversalOfMovementId { get; private set; }
    public string? Notes { get; private set; }

    private StockMovement() { }

    public static StockMovement Record(
        Guid organizationId,
        Guid groupId,
        StockMovementType type,
        DateTimeOffset occurredAt,
        Guid stockItemId,
        Guid stockLotId,
        Guid warehouseId,
        int direction,
        decimal quantity,
        decimal unitCost,
        decimal quantityAfter,
        decimal averageCostAfter,
        string sourceKind,
        Guid sourceId,
        Guid? sourceLineId,
        Guid? performedBy,
        Guid? reversalOfMovementId = null,
        string? notes = null)
    {
        if (direction is not (1 or -1))
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "El sentido es +1 o −1.");
        }
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad de un movimiento es positiva.");
        }
        if (unitCost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitCost), "El costo no puede ser negativo.");
        }
        return new StockMovement
        {
            Id = Guid.CreateVersion7(occurredAt),
            OrganizationId = organizationId,
            GroupId = groupId,
            Type = type,
            OccurredAt = occurredAt,
            StockItemId = stockItemId,
            StockLotId = stockLotId,
            WarehouseId = warehouseId,
            Direction = direction,
            Quantity = quantity,
            UnitCost = Math.Round(unitCost, 4, MidpointRounding.AwayFromZero),
            TotalCost = Math.Round(quantity * unitCost, 4, MidpointRounding.AwayFromZero),
            QuantityAfter = quantityAfter,
            AverageCostAfter = Math.Round(averageCostAfter, 4, MidpointRounding.AwayFromZero),
            SourceKind = sourceKind,
            SourceId = sourceId,
            SourceLineId = sourceLineId,
            PerformedBy = performedBy,
            ReversalOfMovementId = reversalOfMovementId,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
        };
    }
}

/// <summary>
/// Saldo materializado por (bodega, lote). Es caché del libro: existe para que el mostrador no
/// sume el libro entero en cada venta. Nunca negativo (P6): una restricción de la base lo impide
/// aunque el código se equivoque.
/// </summary>
public sealed class StockBalance
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid StockItemId { get; private set; }
    public Guid StockLotId { get; private set; }
    public decimal QuantityOnHand { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockBalance() { }
}

/// <summary>
/// Costo promedio ponderado móvil por (artículo, bodega) — D13.
///
/// <para>Se recalcula en cada entrada: <c>(existencia × promedio + cantidad × costo) / (existencia
/// + cantidad)</c>. Las salidas no lo cambian; solo se llevan el promedio vigente. Es por bodega
/// porque la misma caja comprada en dos sedes a precios distintos cuesta distinto en cada una.</para>
///
/// <para>La fila también es el <b>candado</b> del artículo en la bodega: todo movimiento la bloquea
/// primero (<c>SELECT … FOR UPDATE</c>), así dos despachos simultáneos de la última unidad se
/// atienden de a uno y el segundo encuentra la existencia en cero.</para>
/// </summary>
public sealed class StockItemCost
{
    public Guid OrganizationId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid StockItemId { get; private set; }

    /// <summary>Existencia total del artículo en la bodega (suma de sus lotes).</summary>
    public decimal QuantityOnHand { get; private set; }

    public decimal AverageCost { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private StockItemCost() { }

    /// <summary>
    /// Promedio después de una entrada. Si no había existencia, el promedio es el costo de lo que
    /// entra: el histórico de una bodega vacía no dice nada del precio de hoy.
    /// </summary>
    public static decimal AverageAfterReceipt(decimal onHand, decimal average, decimal quantity, decimal unitCost)
    {
        if (quantity <= 0)
        {
            return average;
        }
        if (onHand <= 0)
        {
            return unitCost;
        }
        return (onHand * average + quantity * unitCost) / (onHand + quantity);
    }

    /// <summary>
    /// Promedio después de deshacer una entrada (anular una compra): sale lo que entró a su mismo
    /// costo. Si la bodega queda vacía, se conserva el último promedio; si la cuenta diera negativo
    /// (el promedio ya se movió por otras entradas), se conserva el promedio vigente.
    /// </summary>
    public static decimal AverageAfterReceiptReversal(decimal onHand, decimal average, decimal quantity, decimal unitCost)
    {
        var resto = onHand - quantity;
        if (resto <= 0)
        {
            return average;
        }
        var nuevo = (onHand * average - quantity * unitCost) / resto;
        return nuevo < 0 ? average : nuevo;
    }
}
