using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Billing;

public sealed record GetInvoicePdfResult(byte[] Bytes, string FileName);

/// <summary>
/// Arma el PDF de una factura ya emitida (FAC-5).
///
/// <para><b>No consulta el catálogo ni el tarifario.</b> Todo sale de la factura y su documento
/// fiscal, que se congelaron al emitir. Es lo que hace que reimprimir dentro de cinco años
/// produzca el mismo papel —el ítem puede estar borrado, la tasa puede haber cambiado y el CAI
/// puede no existir en la tabla—.</para>
/// </summary>
public sealed class GetInvoicePdfHandler(
    IInvoiceRepository invoices,
    IFiscalRepository fiscal,
    IClinicClock clock,
    IInvoicePdfRenderer renderer)
{
    public async Task<GetInvoicePdfResult?> HandleAsync(
        Guid orgId,
        Guid invoiceId,
        InvoicePrintFormat format = InvoicePrintFormat.Letter,
        bool isReprint = false,
        CancellationToken ct = default)
    {
        var factura = await invoices.GetAsync(orgId, invoiceId, ct);
        if (factura is null)
        {
            return null;
        }

        var documento = await invoices.GetFiscalDocumentAsync(orgId, invoiceId, ct);
        if (documento is null)
        {
            // Un borrador no se imprime: imprimir algo sin número fiscal produce un papel que
            // parece una factura y no lo es.
            return null;
        }

        var regimen = await fiscal.GetActiveRegimeAsync(orgId, clock.Today(), ct);
        var tipo = await fiscal.GetDocumentTypeAsync(orgId, documento.DocumentTypeId, ct);

        var modelo = new InvoicePdfModel(
            Issuer: new PdfIssuerInfo(
                regimen?.IssuerLegalName ?? "—",
                documento.IssuerTaxId ?? regimen?.IssuerTaxId,
                Address: null,
                Phone: null),
            Buyer: new PdfBuyerInfo(documento.BuyerName, documento.BuyerTaxId),
            Fiscal: new PdfFiscalInfo(
                documento.FullNumber,
                tipo?.Name ?? "Factura",
                documento.IssuedAt,
                documento.CaiSnapshot,
                documento.RangeSnapshot,
                documento.LimitDateSnapshot,
                documento.Status == FiscalDocumentStatus.Voided),
            Lines: factura.Lines
                .OrderBy(l => l.LineNumber)
                .Select(l => new PdfInvoiceLine(
                    l.LineNumber, l.DescriptionSnapshot, l.Quantity, l.UnitPrice,
                    l.DiscountAmount, l.Total, MarcaTributaria(l.TaxTreatment, l.TaxRate)))
                .ToList(),
            Totals: new PdfInvoiceTotals(
                documento.SubtotalExempt, documento.SubtotalExonerated,
                documento.SubtotalTaxed15, documento.SubtotalTaxed18,
                documento.DiscountTotal, documento.TaxTotal, documento.GrandTotal,
                documento.AmountInWords),
            IsReprint: isReprint,
            VoidReason: documento.VoidReason);

        var bytes = renderer.Render(modelo, format);
        return new GetInvoicePdfResult(bytes, $"{documento.FullNumber}.pdf");
    }

    /// <summary>
    /// La marca corta que va junto a cada línea. Sale impresa porque una factura mixta tiene que
    /// poder auditarse línea por línea sin recalcular a qué subtotal fue cada una.
    /// </summary>
    private static string MarcaTributaria(TaxTreatment treatment, decimal rate) => treatment switch
    {
        TaxTreatment.Exempt => "E",
        TaxTreatment.Exonerated => "X",
        TaxTreatment.NotSubject => "NS",
        _ => $"G{rate:0.##}",
    };
}
