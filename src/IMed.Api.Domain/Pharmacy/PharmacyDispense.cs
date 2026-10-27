namespace IMed.Api.Domain.Pharmacy;

/// <summary>De dónde salió la entrega: una receta de consulta o el mostrador.</summary>
public enum PharmacyDispenseKind
{
    /// <summary>Despacho de una receta de consulta (D6, P10). Se carga a la cuenta de la consulta.</summary>
    Prescription = 0,

    /// <summary>Venta libre de mostrador (D6, P11). Abre su propia cuenta y se factura en el acto.</summary>
    OverTheCounter = 1,
}

public enum PharmacyDispenseStatus
{
    /// <summary>Entregado: salió del inventario y quedó cargado.</summary>
    Completed = 0,

    /// <summary>
    /// Anulado antes de facturarse (solo venta libre): el inventario volvió y el cargo se reversó.
    /// Lo facturado no se anula aquí; se corrige con nota de crédito (D10).
    /// </summary>
    Cancelled = 1,
}

/// <summary>
/// 🔴 Una entrega de farmacia (FSF §9.1–9.2): despacho de receta o venta libre.
///
/// <para><b>La costura más delicada del módulo</b> (FSF §10.1, costura 1): entregar un medicamento
/// es, en una sola transacción, (a) la salida del lote en el libro, (b) el cargo en la cuenta y (c)
/// el saldo actualizado. Si una de las tres falla, fallan las tres. Esta fila es la cabecera de ese
/// acto y la llave de su idempotencia.</para>
///
/// <para><b>Idempotente por <see cref="IdempotencyKey"/>.</b> La pantalla genera la llave al abrir
/// el despacho; un doble clic, un reintento por red lenta o dos pestañas mandan la misma, y el
/// índice único hace que la segunda devuelva la primera en vez de despachar dos veces.</para>
/// </summary>
public sealed class PharmacyDispense
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public PharmacyDispenseKind Kind { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;

    /// <summary>Sede donde se entregó: decide la razón social que factura (FAC-9).</summary>
    public Guid LocationId { get; private set; }

    public Guid WarehouseId { get; private set; }

    /// <summary>Cuenta donde quedaron los cargos.</summary>
    public Guid AccountId { get; private set; }

    public string? PatientFhirId { get; private set; }
    public string? PatientName { get; private set; }

    /// <summary>Consulta de la receta, si es despacho de receta.</summary>
    public string? EncounterFhirId { get; private set; }

    /// <summary>Comprador de la venta libre (nombre y RTN para la factura), si lo dio.</summary>
    public string? BuyerName { get; private set; }
    public string? BuyerTaxId { get; private set; }

    public PharmacyDispenseStatus Status { get; private set; }
    public DateTimeOffset DispensedAt { get; private set; }
    public Guid? DispensedBy { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Factura emitida (venta libre).</summary>
    public Guid? InvoiceId { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public string? CancellationReason { get; private set; }

    private readonly List<PharmacyDispenseLine> _lines = [];
    public IReadOnlyList<PharmacyDispenseLine> Lines => _lines;

    private PharmacyDispense() { }

    public static PharmacyDispense Start(
        Guid organizationId,
        PharmacyDispenseKind kind,
        string idempotencyKey,
        Guid locationId,
        Guid warehouseId,
        string? patientFhirId,
        string? patientName,
        string? encounterFhirId,
        Guid? dispensedBy,
        string? notes = null,
        string? buyerName = null,
        string? buyerTaxId = null,
        TimeProvider? timeProvider = null)
    {
        var llave = (idempotencyKey ?? string.Empty).Trim();
        if (llave.Length is 0 or > 64)
        {
            throw new ArgumentException(
                "La entrega necesita su llave de idempotencia (hasta 64 caracteres).", nameof(idempotencyKey));
        }
        if (kind == PharmacyDispenseKind.Prescription
            && (string.IsNullOrWhiteSpace(patientFhirId) || string.IsNullOrWhiteSpace(encounterFhirId)))
        {
            throw new ArgumentException("El despacho de una receta necesita el paciente y la consulta.", nameof(encounterFhirId));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new PharmacyDispense
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Kind = kind,
            IdempotencyKey = llave,
            LocationId = locationId,
            WarehouseId = warehouseId,
            PatientFhirId = Clean(patientFhirId),
            PatientName = Clean(patientName),
            EncounterFhirId = Clean(encounterFhirId),
            BuyerName = Clean(buyerName),
            BuyerTaxId = Clean(buyerTaxId),
            Status = PharmacyDispenseStatus.Completed,
            DispensedAt = now,
            DispensedBy = dispensedBy,
            Notes = Clean(notes),
        };
    }

    /// <summary>La cuenta se conoce después de abrir la cabecera (la venta libre la abre con su id).</summary>
    public void AssignAccount(Guid accountId) => AccountId = accountId;

    public PharmacyDispenseLine AddLine(
        Guid stockItemId,
        string description,
        decimal quantity,
        string? medicationRequestFhirId = null,
        string? prescribedAtcCode = null,
        string? prescribedText = null,
        decimal? prescribedQuantity = null,
        string? prescribedUnit = null,
        bool completesPrescription = true,
        string? substitutionReason = null)
    {
        var linea = PharmacyDispenseLine.Create(
            Id, _lines.Count + 1, stockItemId, description, quantity, medicationRequestFhirId,
            prescribedAtcCode, prescribedText, prescribedQuantity, prescribedUnit, completesPrescription,
            substitutionReason);
        _lines.Add(linea);
        return linea;
    }

    public void MarkInvoiced(Guid invoiceId) => InvoiceId = invoiceId;

    public void Cancel(Guid? by, string reason, TimeProvider? timeProvider = null)
    {
        if (Kind != PharmacyDispenseKind.OverTheCounter)
        {
            throw new InvalidOperationException(
                "Un despacho de receta no se anula: si el paciente devuelve el medicamento, se emite una nota de crédito.");
        }
        if (Status == PharmacyDispenseStatus.Cancelled)
        {
            throw new InvalidOperationException("La venta ya está anulada.");
        }
        if (InvoiceId is not null)
        {
            throw new InvalidOperationException(
                "La venta ya se facturó: se corrige con una nota de crédito, no anulándola aquí.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Anular una venta exige motivo.", nameof(reason));
        }
        Status = PharmacyDispenseStatus.Cancelled;
        CancelledAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        CancelledBy = by;
        CancellationReason = reason.Trim();
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

/// <summary>
/// Una línea entregada: un artículo, su cantidad y de qué lotes salió.
///
/// <para><b>Parcial y sustitución de primera clase</b> (P10, FSF §9.1). En Honduras es normal que
/// el paciente compre 10 de las 21 tabletas: <see cref="CompletesPrescription"/> en falso deja la
/// receta en la cola. Y si farmacia entrega un artículo con otro principio activo que el recetado,
/// queda la constancia en <see cref="SubstitutionReason"/>.</para>
/// </summary>
public sealed class PharmacyDispenseLine
{
    public Guid Id { get; private set; }
    public Guid DispenseId { get; private set; }
    public int LineNumber { get; private set; }
    public Guid StockItemId { get; private set; }

    /// <summary>Descripción del artículo entregado, congelada.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Cantidad entregada, en unidad base.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Receta (<c>MedicationRequest</c>) que se despachó, si es despacho de receta.</summary>
    public string? MedicationRequestFhirId { get; private set; }

    public string? PrescribedAtcCode { get; private set; }
    public string? PrescribedText { get; private set; }
    public decimal? PrescribedQuantity { get; private set; }
    public string? PrescribedUnit { get; private set; }

    /// <summary>Con esta entrega la receta queda atendida. En falso, sigue en la cola (parcial).</summary>
    public bool CompletesPrescription { get; private set; }

    /// <summary>Constancia de sustitución: por qué se entregó otro principio activo que el recetado.</summary>
    public string? SubstitutionReason { get; private set; }

    /// <summary>Cargo que generó la línea (su origen es esta línea: <c>PharmacyDispense</c> + id).</summary>
    public Guid? ChargeId { get; private set; }

    private readonly List<PharmacyDispenseLot> _lots = [];
    public IReadOnlyList<PharmacyDispenseLot> Lots => _lots;

    private PharmacyDispenseLine() { }

    internal static PharmacyDispenseLine Create(
        Guid dispenseId, int lineNumber, Guid stockItemId, string description, decimal quantity,
        string? medicationRequestFhirId, string? prescribedAtcCode, string? prescribedText,
        decimal? prescribedQuantity, string? prescribedUnit, bool completesPrescription, string? substitutionReason)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "La cantidad entregada tiene que ser positiva.");
        }
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("La línea necesita la descripción del artículo.", nameof(description));
        }
        return new PharmacyDispenseLine
        {
            Id = Guid.CreateVersion7(),
            DispenseId = dispenseId,
            LineNumber = lineNumber,
            StockItemId = stockItemId,
            Description = description.Trim(),
            Quantity = quantity,
            MedicationRequestFhirId = string.IsNullOrWhiteSpace(medicationRequestFhirId) ? null : medicationRequestFhirId.Trim(),
            PrescribedAtcCode = string.IsNullOrWhiteSpace(prescribedAtcCode) ? null : prescribedAtcCode.Trim().ToUpperInvariant(),
            PrescribedText = string.IsNullOrWhiteSpace(prescribedText) ? null : prescribedText.Trim(),
            PrescribedQuantity = prescribedQuantity,
            PrescribedUnit = string.IsNullOrWhiteSpace(prescribedUnit) ? null : prescribedUnit.Trim(),
            CompletesPrescription = completesPrescription,
            SubstitutionReason = string.IsNullOrWhiteSpace(substitutionReason) ? null : substitutionReason.Trim(),
        };
    }

    public void AddLot(Guid stockLotId, decimal quantity, decimal unitCost)
    {
        _lots.Add(PharmacyDispenseLot.Create(Id, stockLotId, quantity, unitCost));
    }

    public void LinkCharge(Guid chargeId) => ChargeId = chargeId;
}

/// <summary>
/// De qué lote salió cada parte de una línea (FSF §8: el lote de la dispensación es obligatorio).
/// Es lo que permite, ante un retiro de lote, saber a qué pacientes se les entregó, y devolver al
/// lote original cuando hay nota de crédito (D10).
/// </summary>
public sealed class PharmacyDispenseLot
{
    public Guid Id { get; private set; }
    public Guid DispenseLineId { get; private set; }
    public Guid StockLotId { get; private set; }
    public decimal Quantity { get; private set; }

    /// <summary>Costo promedio con el que salió: la devolución entra al mismo costo.</summary>
    public decimal UnitCost { get; private set; }

    /// <summary>Cuánto de esta porción ya volvió por notas de crédito.</summary>
    public decimal ReturnedQuantity { get; private set; }

    private PharmacyDispenseLot() { }

    internal static PharmacyDispenseLot Create(Guid lineId, Guid stockLotId, decimal quantity, decimal unitCost) => new()
    {
        Id = Guid.CreateVersion7(),
        DispenseLineId = lineId,
        StockLotId = stockLotId,
        Quantity = quantity,
        UnitCost = unitCost,
    };

    public decimal Returnable => Quantity - ReturnedQuantity;

    public void RegisterReturn(decimal quantity)
    {
        if (quantity <= 0 || quantity > Returnable)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "No se puede devolver más de lo que salió del lote.");
        }
        ReturnedQuantity += quantity;
    }
}

/// <summary>
/// Constancia de una devolución al inventario por nota de crédito (B6). Su llave única
/// (nota de crédito + cargo) es lo que vuelve idempotente al puerto: la nota de crédito puede
/// reintentarse sin que la mercadería entre dos veces.
/// </summary>
public sealed class StockReturnRecord
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CreditNoteId { get; private set; }
    public Guid ChargeId { get; private set; }
    public Guid StockItemId { get; private set; }
    public decimal Quantity { get; private set; }

    /// <summary>Cantidad que efectivamente volvió (puede ser menor si ya se había devuelto antes).</summary>
    public decimal ReturnedQuantity { get; private set; }

    /// <summary>Por qué no volvió todo, si no volvió todo.</summary>
    public string? Note { get; private set; }

    public DateTimeOffset ProcessedAt { get; private set; }

    private StockReturnRecord() { }

    public static StockReturnRecord Create(
        Guid organizationId, Guid creditNoteId, Guid chargeId, Guid stockItemId, decimal quantity,
        decimal returnedQuantity, string? note, TimeProvider? timeProvider = null) => new()
    {
        Id = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        CreditNoteId = creditNoteId,
        ChargeId = chargeId,
        StockItemId = stockItemId,
        Quantity = quantity,
        ReturnedQuantity = returnedQuantity,
        Note = note,
        ProcessedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
    };
}
