namespace IMed.Api.Domain.Billing.Cash;

/// <summary>Con qué se pagó.</summary>
public enum PaymentMethod
{
    Cash = 0,
    Card = 1,
    Transfer = 2,
    Check = 3,

    /// <summary>Se lleva el servicio y paga después. No entra al efectivo del arqueo.</summary>
    Credit = 4,
}

public enum PaymentStatus
{
    Applied = 0,

    /// <summary>Revertido: el cobro se deshizo. Se conserva para poder explicar el movimiento.</summary>
    Reversed = 1,
}

public enum CashSessionStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>Movimientos de caja que no son venta.</summary>
public enum CashMovementKind
{
    /// <summary>Retiro parcial: el efectivo sale de la caja hacia la bóveda o el banco.</summary>
    Withdrawal = 0,

    /// <summary>Ingreso de efectivo que no viene de una venta — cambio, reposición de sencillo.</summary>
    Deposit = 1,

    /// <summary>Ajuste declarado por el cajero, siempre con motivo.</summary>
    Adjustment = 2,
}
