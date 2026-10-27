namespace IMed.Api.Domain.Inventory;

/// <summary>
/// Qué clase de artículo es (FSF §8, recortado por D8). Decide el perfil que lleva y si el lote es
/// obligatorio: medicamentos y reactivos siempre llevan lote; insumos y material, si se quiere.
/// </summary>
public enum StockItemKind
{
    /// <summary>Medicamento: se enlaza al catálogo genérico por su código ATC.</summary>
    Medication = 0,

    /// <summary>Reactivo de laboratorio.</summary>
    Reagent = 1,

    /// <summary>Insumo o material (gasas, jeringas, guantes, papelería clínica).</summary>
    Supply = 2,
}

/// <summary>Para qué se usa una bodega. Es descriptivo: no cambia las reglas de existencia.</summary>
public enum WarehouseKind
{
    /// <summary>La bodega general que cada sede trae de fábrica (P7).</summary>
    General = 0,

    /// <summary>Farmacia: de aquí despacha y vende la farmacia por defecto.</summary>
    Pharmacy = 1,

    /// <summary>Laboratorio.</summary>
    Laboratory = 2,

    /// <summary>Consultorio o área de servicio.</summary>
    Clinic = 3,

    Other = 9,
}

/// <summary>
/// Tipo de movimiento del libro. Es estructural (enum en código, FSF §3): cada tipo tiene un signo y
/// un documento de origen conocidos, y los reportes se apoyan en eso.
/// </summary>
public enum StockMovementType
{
    /// <summary>Entrada por compra (entrada de mercadería, D7).</summary>
    PurchaseReceipt = 0,

    /// <summary>Anulación de una entrada de compra: sale lo que entró, a su mismo costo.</summary>
    PurchaseReceiptVoid = 1,

    /// <summary>Salida de la bodega de origen en un traslado.</summary>
    TransferOut = 2,

    /// <summary>Entrada a la bodega de destino en un traslado.</summary>
    TransferIn = 3,

    /// <summary>Ajuste que suma (sobrante, corrección de conteo).</summary>
    AdjustmentIn = 4,

    /// <summary>Ajuste que resta (daño, vencido, pérdida, corrección de conteo).</summary>
    AdjustmentOut = 5,

    /// <summary>Consumo interno manual (D9).</summary>
    InternalConsumption = 6,

    /// <summary>Despacho de una receta de consulta (D6).</summary>
    Dispense = 7,

    /// <summary>Venta libre de mostrador (D6, P11).</summary>
    Sale = 8,

    /// <summary>Devolución del cliente por nota de crédito (D10): vuelve al lote original.</summary>
    CustomerReturn = 9,

    /// <summary>Anulación de una venta libre que no llegó a facturarse.</summary>
    SaleCancellation = 10,
}

/// <summary>Estado de un documento de inventario (ajuste o conteo).</summary>
public enum StockDocumentStatus
{
    /// <summary>En preparación: un conteo con cantidades por capturar.</summary>
    Draft = 0,

    /// <summary>Supera el monto de aprobación y espera al gerente de inventario (P8).</summary>
    PendingApproval = 1,

    /// <summary>Aplicado al libro.</summary>
    Posted = 2,

    /// <summary>Rechazado o cancelado: no movió nada.</summary>
    Rejected = 3,
}

/// <summary>
/// Motivo de un ajuste (P8: obligatorio y de una lista). Lista cerrada a propósito: con texto libre
/// no se puede medir por qué se pierde inventario, que es la pregunta del gerente.
/// </summary>
public enum AdjustmentReason
{
    /// <summary>Producto dañado o roto.</summary>
    Damaged = 0,

    /// <summary>Producto vencido que se da de baja.</summary>
    Expired = 1,

    /// <summary>Pérdida o faltante sin explicación.</summary>
    Loss = 2,

    /// <summary>Diferencia encontrada en un conteo físico.</summary>
    CountDifference = 3,

    /// <summary>Sobrante encontrado (existencia que no estaba registrada).</summary>
    Found = 4,

    /// <summary>Corrección de un error de registro anterior.</summary>
    EntryError = 5,

    /// <summary>Donación o muestra médica recibida sin factura.</summary>
    Donation = 6,

    Other = 9,
}

/// <summary>Estado de un lote. Bloquear un lote lo saca del FEFO sin borrar su historia.</summary>
public enum StockLotStatus
{
    Available = 0,

    /// <summary>Bloqueado (retiro del fabricante, cuarentena): no se despacha ni se vende.</summary>
    Blocked = 1,
}
