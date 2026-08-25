namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Cómo trata la ley a un cobrable. <b>No son sinónimos</b> y confundirlos falsea la declaración.
///
/// <para>En Honduras el grueso de la facturación de una clínica es <see cref="Exento"/>: los
/// servicios médicos, hospitalarios, de laboratorio clínico y radiológicos lo están, y los
/// medicamentos también. Pero no todo — los tratamientos de belleza estética pagan 15%, y una
/// farmacia vende cosas que no son medicamento (cosméticos, suplementos, biberones) que también
/// pagan. Por eso la tasa nunca puede ser una constante del código: es atributo del ítem.</para>
/// </summary>
public enum TaxTreatment
{
    /// <summary>La ley lo grava y se cobra la tasa.</summary>
    Taxed = 0,

    /// <summary>La ley no lo grava. No es lo mismo que gravado al 0%.</summary>
    Exempt = 1,

    /// <summary>
    /// Está gravado, pero este comprador tiene dispensa y presenta constancia. La diferencia con
    /// <see cref="Exempt"/> es quién decide: acá es el comprador quien exime, no el producto.
    /// </summary>
    Exonerated = 2,

    /// <summary>Fuera del ámbito del tributo.</summary>
    NotSubject = 3,
}

/// <summary>Estado de una autorización de numeración.</summary>
public enum FiscalAuthorizationStatus
{
    Active = 0,

    /// <summary>Se consumió el último correlativo del rango.</summary>
    Exhausted = 1,

    /// <summary>Pasó la fecha límite de emisión.</summary>
    Expired = 2,

    /// <summary>Se dio de baja antes de agotarse.</summary>
    Revoked = 3,
}

/// <summary>Qué clase de trabajo hace una caja. Decide la plantilla de impresión y qué se le ofrece.</summary>
public enum CashPointProfile
{
    Services = 0,
    Pharmacy = 1,
    Mixed = 2,
}

/// <summary>
/// Por qué no se pudo reservar un número. Se distinguen porque cada una tiene una salida
/// distinta para el usuario: pedir un CAI nuevo, renovar el vencido, o llamar al administrador.
/// </summary>
public enum FiscalReservationFailure
{
    /// <summary>No existe la autorización pedida.</summary>
    NotFound = 0,

    /// <summary>Se acabaron los correlativos del rango.</summary>
    Exhausted = 1,

    /// <summary>Pasó la fecha límite de emisión.</summary>
    Expired = 2,

    /// <summary>Está dada de baja.</summary>
    Revoked = 3,
}
