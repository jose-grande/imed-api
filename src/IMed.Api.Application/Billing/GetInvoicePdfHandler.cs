using System.Text.RegularExpressions;
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
    ILegalEntityRepository legalEntities,
    ICreditNoteRepository creditNotes,
    IFiscalAdapterResolver adapters)
{
    public async Task<GetInvoicePdfResult?> HandleAsync(
        Guid orgId,
        Guid invoiceId,
        InvoicePrintFormat format = InvoicePrintFormat.Letter,
        bool isReprint = false,
        CancellationToken ct = default)
    {
        // El id puede ser de una factura o de una nota de crédito (A9): las dos se imprimen igual,
        // la nota con la referencia a la factura que modifica.
        var factura = await invoices.GetAsync(orgId, invoiceId, ct);
        var nota = factura is null ? await creditNotes.GetAsync(orgId, invoiceId, ct) : null;
        if (factura is null && nota is null)
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

        var establecimiento = documento.EstablishmentAddress ?? sede?.Address;
        var casaMatriz = documento.IssuerFiscalAddress ?? razon?.FiscalAddress;

        // ISV por tarifa (art. 11.1.i), de las líneas congeladas: son las que sumaron el total.
        var tributos = factura is not null
            ? factura.Lines.Where(l => l.TaxTreatment == TaxTreatment.Taxed).Select(l => (l.TaxRate, l.TaxAmount))
            : nota!.Lines.Where(l => l.TaxTreatment == TaxTreatment.Taxed).Select(l => (l.TaxRate, l.TaxAmount));
        var isv = tributos.ToList();
        var isv15 = Math.Round(isv.Where(t => t.TaxRate == 15m).Sum(t => t.TaxAmount), 2, MidpointRounding.AwayFromZero);
        var isv18 = Math.Round(isv.Where(t => t.TaxRate == 18m).Sum(t => t.TaxAmount), 2, MidpointRounding.AwayFromZero);

        FiscalDocument? original = nota is null ? null : await invoices.GetFiscalDocumentAsync(orgId, nota.InvoiceId, ct);

        var modelo = new InvoicePdfModel(
            Issuer: new PdfIssuerInfo(
                documento.IssuerLegalName ?? razon?.LegalName ?? regimen?.IssuerLegalName ?? "—",
                documento.IssuerTaxId ?? razon?.TaxId ?? regimen?.IssuerTaxId,
                Address: establecimiento ?? casaMatriz,
                Phone: sede?.Phone ?? razon?.Phone,
                Logo: logo?.ToPdfLogo(orgId),
                TradeName: documento.IssuerTradeName ?? razon?.TradeName,
                HeadOfficeAddress: establecimiento is not null
                    && !string.Equals(casaMatriz?.Trim(), establecimiento.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? casaMatriz : null,
                Email: razon?.Email),
            Buyer: new PdfBuyerInfo(documento.BuyerName, documento.BuyerTaxId,
                BuyerExemption.Normalize(new BuyerExemption(
                    documento.BuyerExemptPurchaseOrder, documento.BuyerExemptRegistry, documento.BuyerSagRegistry))),
            Fiscal: new PdfFiscalInfo(
                documento.FullNumber,
                tipo?.Name ?? "Factura",
                documento.IssuedAt,
                documento.CaiSnapshot,
                RangoCompleto(documento, regimen?.RegimeCode),
                documento.LimitDateSnapshot,
                documento.Status == FiscalDocumentStatus.Voided),
            Lines: factura is not null
                ? factura.Lines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new PdfInvoiceLine(
                        l.LineNumber, l.DescriptionSnapshot, l.Quantity, l.UnitPrice,
                        l.DiscountAmount, l.Total, MarcaTributaria(l.TaxTreatment, l.TaxRate)))
                    .ToList()
                : nota!.Lines
                    .OrderBy(l => l.LineNumber)
                    .Select(l => new PdfInvoiceLine(
                        l.LineNumber, l.DescriptionSnapshot, l.Quantity, l.UnitPrice,
                        l.DiscountAmount, l.Total, MarcaTributaria(l.TaxTreatment, l.TaxRate)))
                    .ToList(),
            Totals: new PdfInvoiceTotals(
                documento.SubtotalExempt, documento.SubtotalExonerated,
                documento.SubtotalTaxed15, documento.SubtotalTaxed18,
                documento.DiscountTotal, documento.TaxTotal, documento.GrandTotal,
                documento.AmountInWords,
                Tax15: isv15,
                Tax18: isv18),
            IsReprint: isReprint,
            VoidReason: documento.VoidReason,
            Reference: nota is null ? null : $"Motivo: {nota.Reason}",
            Modifies: nota is null ? null
                : new PdfModifiedDocument(original?.FullNumber ?? "—", original?.CaiSnapshot ?? "—",
                    original?.IssuedAt ?? DateTimeOffset.MinValue));

        var bytes = renderer.Render(modelo, format);
        return new GetInvoicePdfResult(bytes, $"{documento.FullNumber}.pdf");
    }

    /// <summary>
    /// El rango como lo emite el SAR (art. 10.5). Los documentos emitidos antes de guardarlo así
    /// congelaron solo <c>desde-hasta</c>; se recompone con los segmentos del propio documento,
    /// que son los de su autorización.
    /// </summary>
    private string RangoCompleto(FiscalDocument d, string? regimeCode)
    {
        var m = Regex.Match(d.RangeSnapshot, @"^\s*(\d+)\s*-\s*(\d+)\s*$");
        if (!m.Success)
        {
            return d.RangeSnapshot;
        }
        var adapter = adapters.Find(regimeCode ?? HondurasRegime);
        if (adapter is null)
        {
            return d.RangeSnapshot;
        }
        try
        {
            return adapter.ComposeRange(
                d.EstablishmentCode, d.EmissionPointCode, d.DocumentTypeCode,
                long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is ArgumentException)
        {
            return d.RangeSnapshot;
        }
    }

    private const string HondurasRegime = "HN_SAR_CAI";

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
