namespace IMed.Api.Domain.Billing.Invoicing;

/// <summary>A quién se le factura. Un mismo episodio puede generar dos facturas distintas.</summary>
public enum BillToKind
{
    Patient = 0,

    /// <summary>Aseguradora o empresa con convenio. Sin uso hasta U6; se declara para no migrar después.</summary>
    Payer = 1,
}

public enum InvoiceStatus
{
    /// <summary>Armada, sin timbrar. Todavía se puede cambiar.</summary>
    Draft = 0,

    /// <summary>Timbrada. Inmutable.</summary>
    Issued = 1,

    Paid = 2,
    PartiallyPaid = 3,

    /// <summary>Anulada dentro de ventana. El correlativo queda consumido.</summary>
    Voided = 4,
}

public enum FiscalDocumentStatus
{
    Issued = 0,
    Voided = 1,
}
