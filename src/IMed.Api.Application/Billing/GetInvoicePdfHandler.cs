using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Organizations;
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
///
/// <para><b>El logo es la única excepción, y es deliberada.</b> Se lee el vigente y no uno
/// congelado: es identidad visual, no dato fiscal. Nada de lo que respalda legalmente el
/// documento —número, CAI, rango, tasas, totales— depende de él, y guardar una copia de la
/// imagen por factura emitida multiplicaría el tamaño de la base sin ganancia alguna.</para>
/// </summary>
public sealed class GetInvoicePdfHandler(
    IInvoiceRepository invoices,
    IFiscalRepository fiscal,
    IClinicClock clock,
    IInvoicePdfRenderer renderer,
    IOrganizationBrandingRepository branding,
    ILocationRepository locations,
    ILegalEntityRepository legalEntities)
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

        var logo = await branding.GetLogoAsync(orgId, ct);

        // El emisor sale de lo congelado en el documento (FAC-9): una reimpresión tiene que ser
        // idéntica al original aunque la razón social o la sede hayan cambiado después. Lo que no
        // se congeló —documentos anteriores a FAC-9— se completa con la sede y su razón social.
        var sede = await locations.GetAsync(orgId, documento.LocationId, ct);
        var razon = sede is null ? null : await legalEntities.GetAsync(orgId, sede.LegalEntityId, ct);

        var modelo = new InvoicePdfModel(
            Issuer: new PdfIssuerInfo(
                documento.IssuerLegalName ?? razon?.LegalName ?? regimen?.IssuerLegalName ?? "—",
                documento.IssuerTaxId ?? razon?.TaxId ?? regimen?.IssuerTaxId,
                Address: documento.EstablishmentAddress ?? documento.IssuerFiscalAddress
                    ?? sede?.Address ?? razon?.FiscalAddress,
                Phone: sede?.Phone ?? razon?.Phone,
                Logo: logo?.ToPdfLogo(orgId)),
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
