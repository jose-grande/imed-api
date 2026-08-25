namespace IMed.Api.Domain.Billing.Accounts;

/// <summary>Qué agrupa una cuenta.</summary>
public enum BillingAccountKind
{
    /// <summary>Un episodio: una consulta, una internación. Se abre y se cierra.</summary>
    Episode = 0,

    /// <summary>Cuenta corriente del paciente o de una empresa. No se cierra por episodio.</summary>
    Running = 1,
}

public enum BillingAccountStatus
{
    Open = 0,
    Closed = 1,
    Invoiced = 2,
    Voided = 3,
}

/// <summary>
/// De dónde vino un cargo. Es la mitad de la clave de idempotencia: junto con
/// <c>SourceId</c> identifica el acto clínico que lo originó.
/// </summary>
public enum ChargeSourceKind
{
    /// <summary>Del cierre de un encuentro o de la confirmación de una cita.</summary>
    Encounter = 0,

    /// <summary>De admitir una orden en la recepción del laboratorio.</summary>
    LabOrder = 1,

    /// <summary>De liberar un resultado validado.</summary>
    LabResult = 2,

    /// <summary>De una dispensación de farmacia (U3).</summary>
    PharmacyDispense = 3,

    /// <summary>Tecleado por alguien. La excepción, y queda marcada como tal.</summary>
    Manual = 9,
}

public enum ChargeStatus
{
    /// <summary>Capturado, todavía no facturado.</summary>
    Captured = 0,

    /// <summary>Ya salió en una factura. No se puede tocar.</summary>
    Invoiced = 1,

    /// <summary>Reversado: no se cobra. Se conserva la fila para poder explicar el movimiento.</summary>
    Reversed = 2,
}
