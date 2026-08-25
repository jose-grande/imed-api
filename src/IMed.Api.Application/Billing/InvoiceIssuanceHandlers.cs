using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Billing;

public sealed record InvoiceLineDto(
    int LineNumber,
    Guid ChargeId,
    string Description,
    string TaxCode,
    string TaxTreatment,
    decimal TaxRate,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountAmount,
    decimal NetAmount,
    decimal TaxAmount,
    decimal Total);

public sealed record FiscalDocumentDto(
    Guid Id,
    string FullNumber,
    string EstablishmentCode,
    string EmissionPointCode,
    string DocumentTypeCode,
    long Sequence,
    DateTimeOffset IssuedAt,
    string BuyerName,
    string? BuyerTaxId,
    decimal SubtotalExempt,
    decimal SubtotalExonerated,
    decimal SubtotalTaxed15,
    decimal SubtotalTaxed18,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    string? AmountInWords,
    string CaiSnapshot,
    string RangeSnapshot,
    DateOnly LimitDateSnapshot,
    string Status,
    string? VoidReason);

public sealed record InvoiceDto(
    Guid Id,
    Guid AccountId,
    Guid BillToPartyId,
    string BillToKind,
    DateOnly IssueDate,
    string Currency,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal Balance,
    string Status,
    IReadOnlyList<InvoiceLineDto> Lines,
    FiscalDocumentDto? FiscalDocument);

/// <summary>Por qué no se pudo emitir. Cada motivo manda al usuario a un lado distinto.</summary>
public sealed record IssueRejection(string Code, string Message);

public sealed record IssueInvoiceResult(InvoiceDto? Invoice, IssueRejection? Rejected)
{
    public bool Succeeded => Invoice is not null;
    public static IssueInvoiceResult Ok(InvoiceDto i) => new(i, null);
    public static IssueInvoiceResult Reject(string code, string message) =>
        new(null, new IssueRejection(code, message));
}

public sealed record IssueInvoiceCommand(
    Guid OrganizationId,
    Guid AccountId,
    Guid LocationId,
    Guid DocumentTypeId,
    Guid? FiscalAuthorizationId = null,
    Guid? BillToPartyId = null,
    string? BuyerTaxId = null,
    string? BuyerName = null);

/// <summary>
/// Emite una factura a partir de los cargos sin facturar de una cuenta (FAC-5).
///
/// <para><b>Una sola transacción, en este orden:</b> armar la factura desde los cargos → validar
/// las reglas del régimen → <b>reservar el correlativo</b> → congelar el documento fiscal →
/// marcar los cargos como facturados. El correlativo se reserva último de las validaciones y
/// primero de las escrituras: reservarlo antes de saber si la venta es válida consume números
/// en cada intento fallido, y cada número consumido es un hueco que hay que explicar.</para>
///
/// <para>🔴 <b>Si la impresión falla, el documento ya existe y se reimprime — jamás se
/// re-emite.</b> Por eso el número no se devuelve al pozo y por eso <c>ux_fiscal_document_invoice</c>
/// impide timbrar dos veces la misma factura.</para>
/// </summary>
public sealed class IssueInvoiceHandler(
    IBillingAccountRepository accounts,
    IInvoiceRepository invoices,
    IFiscalRepository fiscal,
    IPartyRepository parties,
    IFiscalAdapterResolver adapters,
    IClinicClock clock)
{
    public async Task<IssueInvoiceResult> HandleAsync(
        IssueInvoiceCommand cmd, CancellationToken ct = default)
    {
        var today = clock.Today();

        var cuenta = await accounts.GetAccountAsync(cmd.OrganizationId, cmd.AccountId, ct);
        if (cuenta is null)
        {
            return IssueInvoiceResult.Reject("cuenta_inexistente", "La cuenta no existe.");
        }

        // Se leen para modificar: la emisión los marca como facturados en la misma transacción.
        var cargos = await accounts.ListCapturedChargesForUpdateAsync(
            cmd.OrganizationId, cmd.AccountId, ct);

        if (cargos.Count == 0)
        {
            return IssueInvoiceResult.Reject(
                "sin_cargos", "La cuenta no tiene cargos pendientes de facturar.");
        }

        // ── Régimen y adaptador ──
        var regimen = await fiscal.GetActiveRegimeAsync(cmd.OrganizationId, today, ct);
        if (regimen is null)
        {
            return IssueInvoiceResult.Reject(
                "sin_regimen", "La organización no tiene régimen fiscal configurado.");
        }
        var adapter = adapters.Find(regimen.RegimeCode);
        if (adapter is null)
        {
            return IssueInvoiceResult.Reject(
                "regimen_no_soportado",
                $"No hay adaptador para el régimen '{regimen.RegimeCode}'.");
        }

        var tipo = await fiscal.GetDocumentTypeAsync(cmd.OrganizationId, cmd.DocumentTypeId, ct);
        if (tipo is null)
        {
            return IssueInvoiceResult.Reject(
                "tipo_inexistente", "El tipo de documento no existe.");
        }

        // ── Armar la factura comercial ──
        var partyId = cmd.BillToPartyId ?? cuenta.PartyId;
        var parte = await parties.GetAsync(cmd.OrganizationId, partyId, ct);
        var factura = Invoice.Draft(cmd.OrganizationId, cuenta.Id, partyId, today);

        var linea = 1;
        foreach (var c in cargos)
        {
            var tributo = await fiscal.GetTaxCodeAsync(cmd.OrganizationId, c.TaxCodeId, ct);
            factura.AddLine(InvoiceLine.FromCharge(
                cmd.OrganizationId, factura.Id, c.Id, linea++,
                c.DescriptionSnapshot,
                tributo?.Code ?? "?",
                tributo?.Treatment ?? TaxTreatment.Exempt,
                c.TaxRate, c.Quantity, c.UnitPrice, c.DiscountAmount));
        }

        // ── Reglas del régimen, antes de tocar el correlativo ──
        var comprador = cmd.BuyerName
            ?? parte?.LegalName
            ?? throw new InvalidOperationException("La factura necesita un comprador.");
        var rtnComprador = cmd.BuyerTaxId ?? parte?.TaxId;

        if (adapter.ValidateBuyer(factura.GrandTotal, rtnComprador) is { } problema)
        {
            return IssueInvoiceResult.Reject("comprador_incompleto", problema);
        }

        // El emisor sin RTN produce un documento que la autoridad no reconoce. Se verifica antes
        // de reservar el correlativo: fallar después ya habría consumido un número.
        if (string.IsNullOrWhiteSpace(regimen.IssuerTaxId))
        {
            return IssueInvoiceResult.Reject(
                "emisor_sin_rtn",
                "El régimen fiscal no tiene el RTN de la clínica: el documento saldría sin emisor.");
        }

        // ── La sede tiene que tener código de establecimiento ──
        var todas = await fiscal.ListAuthorizationsAsync(cmd.OrganizationId, ct);
        var candidatas = cmd.FiscalAuthorizationId is { } authId
            ? todas.Where(a => a.Id == authId).ToList()
            : todas.Where(a => a.LocationId == cmd.LocationId && a.DocumentTypeId == tipo.Id).ToList();

        // Se prefiere una que pueda emitir; si ninguna puede, se toma igual la más reciente para
        // poder decir POR QUÉ. Filtrar por `CanIssueOn` y quedarse sin candidatas le contestaría
        // al cajero "no hay autorización" cuando el problema real es que se agotó o venció — dos
        // situaciones con salidas distintas.
        var autorizacion = candidatas.FirstOrDefault(a => a.CanIssueOn(today))
            ?? candidatas.OrderByDescending(a => a.IssuedAt).FirstOrDefault();

        if (autorizacion is null)
        {
            return IssueInvoiceResult.Reject(
                "sin_autorizacion",
                "No hay ninguna autorización registrada para esta sede y este tipo de documento.");
        }
        if (autorizacion.BlockedReason(today) is { } bloqueo)
        {
            return IssueInvoiceResult.Reject(
                "autorizacion_bloqueada", DescribirBloqueo(bloqueo, autorizacion));
        }

        var establecimiento = await invoices.GetEstablishmentCodeAsync(
            cmd.OrganizationId, autorizacion.LocationId, ct);
        if (string.IsNullOrWhiteSpace(establecimiento))
        {
            return IssueInvoiceResult.Reject(
                "sede_sin_codigo",
                "La sede no tiene código de establecimiento fiscal: el número saldría incompleto.");
        }

        // ── Reservar el correlativo. A partir de acá el número está consumido. ──
        var reserva = await fiscal.ReserveNextSequenceAsync(
            cmd.OrganizationId, autorizacion.Id, today, ct);
        if (!reserva.Reserved)
        {
            return IssueInvoiceResult.Reject(
                "autorizacion_bloqueada",
                DescribirBloqueo(reserva.Failure!.Value, autorizacion));
        }

        var numero = adapter.ComposeNumber(
            establecimiento, autorizacion.EmissionPointCode, tipo.DocumentCode, reserva.Sequence!.Value);

        var documento = FiscalDocument.Issue(
            cmd.OrganizationId, autorizacion.LocationId, autorizacion.Id, tipo.Id, factura.Id,
            numero.EstablishmentCode, numero.EmissionPointCode, numero.DocumentTypeCode,
            numero.Sequence, numero.FullNumber,
            buyerName: comprador,
            caiSnapshot: autorizacion.Cai,
            rangeSnapshot: $"{autorizacion.RangeFrom}-{autorizacion.RangeTo}",
            limitDateSnapshot: autorizacion.LimitDate,
            subtotalExempt: factura.SubtotalFor(TaxTreatment.Exempt),
            subtotalExonerated: factura.SubtotalFor(TaxTreatment.Exonerated),
            subtotalTaxed15: factura.SubtotalFor(TaxTreatment.Taxed, 15m),
            subtotalTaxed18: factura.SubtotalFor(TaxTreatment.Taxed, 18m),
            discountTotal: factura.DiscountTotal,
            taxTotal: factura.TaxTotal,
            grandTotal: factura.GrandTotal,
            amountInWords: adapter.AmountInWords(factura.GrandTotal),
            issuerTaxId: regimen.IssuerTaxId,
            buyerPartyId: partyId,
            buyerTaxId: rtnComprador);

        factura.MarkIssued(documento.Id);

        foreach (var c in cargos)
        {
            c.MarkInvoiced();
        }

        await invoices.SaveIssuedAsync(factura, documento, cargos, ct);
        return IssueInvoiceResult.Ok(InvoiceMapper.ToDto(factura, documento));
    }

    private static string DescribirBloqueo(FiscalReservationFailure motivo, FiscalAuthorization a) =>
        motivo switch
        {
            FiscalReservationFailure.Exhausted =>
                $"La autorización {a.Cai} se quedó sin correlativos: hay que registrar un rango nuevo.",
            FiscalReservationFailure.Expired =>
                $"La autorización {a.Cai} venció el {a.LimitDate:dd/MM/yyyy}: hay que renovarla.",
            FiscalReservationFailure.Revoked =>
                $"La autorización {a.Cai} está dada de baja.",
            _ => "No se pudo reservar el número fiscal.",
        };
}

public sealed class GetInvoiceHandler(IInvoiceRepository invoices)
{
    public async Task<InvoiceDto?> HandleAsync(Guid orgId, Guid invoiceId, CancellationToken ct = default)
    {
        var factura = await invoices.GetAsync(orgId, invoiceId, ct);
        if (factura is null)
        {
            return null;
        }
        var documento = await invoices.GetFiscalDocumentAsync(orgId, invoiceId, ct);
        return InvoiceMapper.ToDto(factura, documento);
    }
}

public sealed class ListAccountInvoicesHandler(IInvoiceRepository invoices)
{
    public async Task<IReadOnlyList<InvoiceDto>> HandleAsync(
        Guid orgId, Guid accountId, CancellationToken ct = default)
    {
        var facturas = await invoices.ListByAccountAsync(orgId, accountId, ct);
        var lista = new List<InvoiceDto>(facturas.Count);
        foreach (var f in facturas)
        {
            lista.Add(InvoiceMapper.ToDto(f, await invoices.GetFiscalDocumentAsync(orgId, f.Id, ct)));
        }
        return lista;
    }
}

/// <summary>
/// Anula una factura dentro de ventana.
///
/// <para><b>El correlativo queda consumido.</b> No vuelve al pozo ni se reutiliza: los huecos que
/// eso deja son legítimos y explicables ante una auditoría, y un número reutilizado no lo es.
/// Fuera de ventana el camino correcto es la nota de crédito, que es otro documento fiscal con su
/// propio número.</para>
/// </summary>
public sealed class VoidInvoiceHandler(IInvoiceRepository invoices, IClinicClock clock)
{
    /// <summary>
    /// Días dentro de los que se puede anular. Pasada la ventana, el documento ya salió del
    /// control de la clínica —el paciente se lo llevó, el contador lo registró— y desandarlo
    /// exige el rastro que solo deja una nota de crédito.
    /// </summary>
    public const int VoidWindowDays = 1;

    public async Task<IssueInvoiceResult> HandleAsync(
        Guid orgId, Guid invoiceId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return IssueInvoiceResult.Reject("sin_motivo", "Anular exige motivo.");
        }

        var (factura, documento) = await invoices.GetForUpdateAsync(orgId, invoiceId, ct);
        if (factura is null)
        {
            return IssueInvoiceResult.Reject("factura_inexistente", "La factura no existe.");
        }
        if (factura.Status == InvoiceStatus.Voided)
        {
            return IssueInvoiceResult.Ok(InvoiceMapper.ToDto(factura, documento));
        }
        if (factura.Status != InvoiceStatus.Issued)
        {
            return IssueInvoiceResult.Reject(
                "estado_invalido", $"Una factura en estado '{factura.Status}' no se anula.");
        }
        if (documento is null)
        {
            return IssueInvoiceResult.Reject(
                "sin_documento", "La factura no tiene documento fiscal.");
        }

        var dias = clock.Today().DayNumber - factura.IssueDate.DayNumber;
        if (dias > VoidWindowDays)
        {
            return IssueInvoiceResult.Reject(
                "fuera_de_ventana",
                $"La factura {documento.FullNumber} se emitió hace {dias} día(s): fuera de la ventana "
                + $"de {VoidWindowDays}. Emití una nota de crédito.");
        }

        factura.Void();
        documento.Void(reason);
        await invoices.SaveVoidAsync(factura, documento, ct);

        return IssueInvoiceResult.Ok(InvoiceMapper.ToDto(factura, documento));
    }
}

public static class InvoiceMapper
{
    public static InvoiceDto ToDto(Invoice i, FiscalDocument? d) =>
        new(i.Id, i.AccountId, i.BillToPartyId, i.BillToKind.ToString(), i.IssueDate, i.Currency,
            i.Subtotal, i.DiscountTotal, i.TaxTotal, i.GrandTotal, i.Balance, i.Status.ToString(),
            i.Lines.OrderBy(l => l.LineNumber).Select(l => new InvoiceLineDto(
                l.LineNumber, l.ChargeId, l.DescriptionSnapshot, l.TaxCodeSnapshot,
                l.TaxTreatment.ToString(), l.TaxRate, l.Quantity, l.UnitPrice,
                l.DiscountAmount, l.NetAmount, l.TaxAmount, l.Total)).ToList(),
            d is null ? null : ToDto(d));

    public static FiscalDocumentDto ToDto(FiscalDocument d) =>
        new(d.Id, d.FullNumber, d.EstablishmentCode, d.EmissionPointCode, d.DocumentTypeCode,
            d.Sequence, d.IssuedAt, d.BuyerName, d.BuyerTaxId,
            d.SubtotalExempt, d.SubtotalExonerated, d.SubtotalTaxed15, d.SubtotalTaxed18,
            d.DiscountTotal, d.TaxTotal, d.GrandTotal, d.AmountInWords,
            d.CaiSnapshot, d.RangeSnapshot, d.LimitDateSnapshot,
            d.Status.ToString(), d.VoidReason);
}
