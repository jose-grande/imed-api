namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Plantilla de impresión. La elige <c>cash_point.printer_profile</c>: el POS de farmacia usa
/// térmica de 80 mm y la caja de servicios, carta.
/// </summary>
public enum InvoicePrintFormat
{
    /// <summary>Hoja carta. El documento formal que el paciente archiva o presenta.</summary>
    Letter = 0,

    /// <summary>Ticket de 80 mm. Impresora térmica de caja.</summary>
    Thermal80 = 1,
}

/// <summary>
/// Renderer del PDF de factura (FAC-5). Mismo patrón que los renderers de receta y orden: el
/// handler arma el modelo y delega.
///
/// <para><b>Todo el modelo son snapshots.</b> El renderer no consulta nada: recibe lo que se
/// congeló al emitir. Es lo que permite que reimprimir dentro de cinco años produzca el mismo
/// papel, aunque el CAI ya no exista y el ítem se haya borrado del catálogo.</para>
/// </summary>
public interface IInvoicePdfRenderer
{
    byte[] Render(InvoicePdfModel model, InvoicePrintFormat format = InvoicePrintFormat.Letter);
}

public sealed record InvoicePdfModel(
    PdfIssuerInfo Issuer,
    PdfBuyerInfo Buyer,
    PdfFiscalInfo Fiscal,
    IReadOnlyList<PdfInvoiceLine> Lines,
    PdfInvoiceTotals Totals,
    /// <summary>
    /// Copia número N. La primera impresión es la original; de ahí en adelante el papel lo dice.
    /// Un duplicado que no se anuncia como tal es indistinguible del original.
    /// </summary>
    bool IsReprint = false,
    string? VoidReason = null,
    /// <summary>
    /// Documento que modifica (nota de crédito, A9): "Modifica la factura 001-002-01-00000007 —
    /// motivo". El SAR exige que la nota identifique la factura que revierte.
    /// </summary>
    string? Reference = null);

public sealed record PdfIssuerInfo(
    string LegalName,
    string? TaxId,
    string? Address,
    string? Phone,
    PdfLogo? Logo = null);

public sealed record PdfBuyerInfo(string Name, string? TaxId);

/// <summary>Los datos legales del documento, todos congelados al emitir.</summary>
public sealed record PdfFiscalInfo(
    string FullNumber,
    string DocumentTypeName,
    DateTimeOffset IssuedAt,
    string Cai,
    string Range,
    DateOnly LimitDate,
    bool IsVoided);

public sealed record PdfInvoiceLine(
    int Number,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal Total,
    /// <summary>Marca de tratamiento tributario que va junto a la línea (E / G15 / …).</summary>
    string TaxMark);

/// <summary>
/// Los totales como los pide el SAR: subtotales separados por tratamiento, para que el contador
/// pueda declarar sin recalcular.
/// </summary>
public sealed record PdfInvoiceTotals(
    decimal SubtotalExempt,
    decimal SubtotalExonerated,
    decimal SubtotalTaxed15,
    decimal SubtotalTaxed18,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    string? AmountInWords);
