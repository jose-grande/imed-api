using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Cash;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

public sealed record CreditNoteLineDto(
    Guid InvoiceLineId, Guid ChargeId, string Description, decimal Quantity, decimal UnitPrice,
    decimal DiscountAmount, decimal TaxRate, decimal NetAmount, decimal TaxAmount, decimal Total);

public sealed record CreditNoteDto(
    Guid Id,
    Guid InvoiceId,
    string? FullNumber,
    string Reason,
    DateOnly IssueDate,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal GrandTotal,
    decimal AppliedToBalance,
    decimal RefundDue,
    DateTimeOffset? RefundedAt,
    IReadOnlyList<CreditNoteLineDto> Lines);

public static class CreditNoteMapper
{
    public static CreditNoteDto ToDto(CreditNote n, string? fullNumber) =>
        new(n.Id, n.InvoiceId, fullNumber, n.Reason, n.IssueDate, n.Subtotal, n.DiscountTotal, n.TaxTotal,
            n.GrandTotal, n.AppliedToBalance, n.RefundDue, n.RefundedAt,
            n.Lines.OrderBy(l => l.LineNumber).Select(l => new CreditNoteLineDto(
                l.InvoiceLineId, l.ChargeId, l.DescriptionSnapshot, l.Quantity, l.UnitPrice, l.DiscountAmount,
                l.TaxRate, l.NetAmount, l.TaxAmount, l.Total)).ToList());
}

/// <summary>Una línea a acreditar: cuál y cuántas unidades.</summary>
public sealed record CreditNoteLineRequest(Guid InvoiceLineId, decimal Quantity);

/// <param name="Lines">Vacío o null: se acredita todo lo que queda de la factura (nota total).</param>
public sealed record IssueCreditNoteCommand(
    Guid OrganizationId, Guid InvoiceId, string Reason, Guid IssuedBy, IReadOnlyList<CreditNoteLineRequest>? Lines);

public sealed record IssueCreditNoteResult(CreditNoteDto? CreditNote, IssueRejection? Rejected)
{
    public bool Succeeded => CreditNote is not null;
    public static IssueCreditNoteResult Ok(CreditNoteDto n) => new(n, null);
    public static IssueCreditNoteResult Reject(string code, string message) => new(null, new IssueRejection(code, message));
}

/// <summary>
/// Emite una nota de crédito (D10, A9): total o por líneas, con su número fiscal del tipo
/// <c>nota_credito</c> y el CAI de la sede que emitió la factura.
///
/// <list type="bullet">
///   <item>La emite la misma sede —y por lo tanto la misma razón social— que la factura: es el mismo
///   contribuyente el que revierte lo que declaró.</item>
///   <item>Baja primero lo que la factura tenía pendiente; lo que excede ya estaba cobrado y queda
///   como devolución pendiente, que se paga desde una caja.</item>
///   <item>Los productos acreditados vuelven al inventario (<see cref="IStockReturnPort"/>).</item>
/// </list>
/// </summary>
public sealed class IssueCreditNoteHandler(
    IInvoiceRepository invoices,
    ICreditNoteRepository creditNotes,
    IFiscalRepository fiscal,
    IFiscalAdapterResolver adapters,
    ILegalEntityRepository legalEntities,
    ILocationRepository locations,
    IBillingAccountRepository accounts,
    IBillingCatalogRepository catalog,
    IStockReturnPort stockReturn,
    IClinicClock clock,
    ILogger<IssueCreditNoteHandler> logger)
{
    public const string CreditNoteTypeCode = "nota_credito";

    public async Task<IssueCreditNoteResult> HandleAsync(IssueCreditNoteCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            return IssueCreditNoteResult.Reject("sin_motivo", "La nota de crédito exige motivo.");
        }
        var today = clock.Today();

        var (factura, original) = await invoices.GetForUpdateAsync(cmd.OrganizationId, cmd.InvoiceId, ct);
        if (factura is null || original is null)
        {
            return IssueCreditNoteResult.Reject("factura_inexistente", "La factura no existe o no está emitida.");
        }
        if (factura.Status is InvoiceStatus.Voided or InvoiceStatus.Credited or InvoiceStatus.Draft)
        {
            return IssueCreditNoteResult.Reject(
                "estado_invalido", $"Una factura en estado '{factura.Status}' no admite nota de crédito.");
        }

        // ── Qué se acredita ──
        var acreditado = await creditNotes.CreditedQuantitiesAsync(cmd.OrganizationId, factura.Id, ct);
        var vivas = factura.Lines.Where(l => !l.Voided).ToList();
        IReadOnlyList<CreditNoteLineRequest> pedidas = cmd.Lines is { Count: > 0 }
            ? cmd.Lines
            : vivas.Select(l => new CreditNoteLineRequest(l.Id, l.Quantity - acreditado.GetValueOrDefault(l.Id)))
                .Where(r => r.Quantity > 0).ToList();
        if (pedidas.Count == 0)
        {
            return IssueCreditNoteResult.Reject("nada_que_acreditar", "La factura ya está acreditada por completo.");
        }

        CreditNote nota;
        try
        {
            nota = CreditNote.Draft(factura, cmd.Reason, cmd.IssuedBy, today);
            foreach (var p in pedidas)
            {
                var linea = vivas.FirstOrDefault(l => l.Id == p.InvoiceLineId)
                    ?? throw new ArgumentException("Una de las líneas no es de esta factura.");
                nota.AddLine(linea, p.Quantity, acreditado.GetValueOrDefault(linea.Id));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return IssueCreditNoteResult.Reject("lineas_invalidas", ex.Message);
        }

        // ── Numeración: misma sede y razón social que la factura ──
        var regimen = await fiscal.GetActiveRegimeAsync(cmd.OrganizationId, today, ct);
        var adapter = regimen is null ? null : adapters.Find(regimen.RegimeCode);
        if (regimen is null || adapter is null)
        {
            return IssueCreditNoteResult.Reject("sin_regimen", "La organización no tiene régimen fiscal configurado.");
        }
        var tipo = (await fiscal.ListDocumentTypesAsync(cmd.OrganizationId, ct))
            .FirstOrDefault(t => t.Code == CreditNoteTypeCode);
        if (tipo is null)
        {
            return IssueCreditNoteResult.Reject("sin_tipo", "El régimen no tiene el tipo de documento nota de crédito.");
        }

        var autorizaciones = (await fiscal.ListAuthorizationsAsync(cmd.OrganizationId, ct))
            .Where(a => a.LocationId == nota.LocationId && a.DocumentTypeId == tipo.Id)
            .ToList();
        var autorizacion = autorizaciones.FirstOrDefault(a => a.CanIssueOn(today))
            ?? autorizaciones.OrderByDescending(a => a.IssuedAt).FirstOrDefault();
        if (autorizacion is null)
        {
            return IssueCreditNoteResult.Reject(
                "sin_autorizacion",
                "La sede de la factura no tiene un CAI de notas de crédito. Regístrelo en Autorizaciones fiscales.");
        }
        if (autorizacion.BlockedReason(today) is { } bloqueo)
        {
            return IssueCreditNoteResult.Reject(
                "autorizacion_bloqueada", $"La autorización {autorizacion.Cai} no puede emitir ({bloqueo}).");
        }

        var razon = factura.LegalEntityId is { } leId ? await legalEntities.GetAsync(cmd.OrganizationId, leId, ct) : null;
        var rtnEmisor = original.IssuerTaxId ?? razon?.TaxId;
        if (string.IsNullOrWhiteSpace(rtnEmisor))
        {
            return IssueCreditNoteResult.Reject("emisor_sin_rtn", "La razón social de la factura no tiene RTN.");
        }
        var establecimiento = await invoices.GetEstablishmentCodeAsync(cmd.OrganizationId, autorizacion.LocationId, ct);
        if (string.IsNullOrWhiteSpace(establecimiento))
        {
            return IssueCreditNoteResult.Reject("sede_sin_codigo", "La sede no tiene código de establecimiento fiscal.");
        }

        var reserva = await fiscal.ReserveNextSequenceAsync(cmd.OrganizationId, autorizacion.Id, today, ct);
        if (!reserva.Reserved)
        {
            return IssueCreditNoteResult.Reject("autorizacion_bloqueada", "No se pudo reservar el número fiscal.");
        }
        var numero = adapter.ComposeNumber(
            establecimiento, autorizacion.EmissionPointCode, tipo.DocumentCode, reserva.Sequence!.Value);
        var sede = (await locations.ListAsync(cmd.OrganizationId, activeOnly: false, ct))
            .FirstOrDefault(l => l.Id == nota.LocationId);

        var documento = FiscalDocument.Issue(
            cmd.OrganizationId, autorizacion.LocationId, autorizacion.Id, tipo.Id,
            // La clave del documento es la nota: una factura tiene un solo documento propio.
            invoiceId: nota.Id,
            numero.EstablishmentCode, numero.EmissionPointCode, numero.DocumentTypeCode,
            numero.Sequence, numero.FullNumber,
            buyerName: original.BuyerName,
            caiSnapshot: autorizacion.Cai,
            rangeSnapshot: $"{autorizacion.RangeFrom}-{autorizacion.RangeTo}",
            limitDateSnapshot: autorizacion.LimitDate,
            subtotalExempt: nota.SubtotalFor(TaxTreatment.Exempt),
            subtotalExonerated: nota.SubtotalFor(TaxTreatment.Exonerated),
            subtotalTaxed15: nota.SubtotalFor(TaxTreatment.Taxed, 15m),
            subtotalTaxed18: nota.SubtotalFor(TaxTreatment.Taxed, 18m),
            discountTotal: nota.DiscountTotal,
            taxTotal: nota.TaxTotal,
            grandTotal: nota.GrandTotal,
            amountInWords: adapter.AmountInWords(nota.GrandTotal),
            issuerTaxId: rtnEmisor,
            buyerPartyId: original.BuyerPartyId,
            buyerTaxId: original.BuyerTaxId,
            replacesDocumentId: original.Id,
            issuer: new IssuerSnapshot(
                razon?.Id ?? Guid.Empty,
                original.IssuerLegalName ?? razon?.LegalName ?? string.Empty,
                original.IssuerTradeName ?? razon?.TradeName,
                original.IssuerFiscalAddress ?? razon?.FiscalAddress,
                sede?.Address));

        var aplicado = factura.ApplyCredit(nota.GrandTotal);
        nota.MarkIssued(documento.Id, aplicado);
        await creditNotes.SaveIssuedAsync(nota, documento, factura, ct);

        await DevolverAlInventarioAsync(cmd.OrganizationId, nota, ct);
        return IssueCreditNoteResult.Ok(CreditNoteMapper.ToDto(nota, documento.FullNumber));
    }

    /// <summary>
    /// Los productos acreditados vuelven al inventario. Después de guardar la nota y fuera de su
    /// transacción: la nota es un hecho fiscal ya consumado; si el inventario falla, se registra y
    /// el puerto es idempotente para reintentarlo.
    /// </summary>
    private async Task DevolverAlInventarioAsync(Guid orgId, CreditNote nota, CancellationToken ct)
    {
        var devoluciones = new List<StockReturnLine>();
        foreach (var l in nota.Lines)
        {
            var cargo = await accounts.GetChargeAsync(orgId, l.ChargeId, ct);
            var item = cargo is null ? null : await catalog.GetItemAsync(orgId, cargo.BillableItemId, ct);
            if (cargo is not null && item?.StockItemId is { } articulo)
            {
                devoluciones.Add(new StockReturnLine(nota.Id, cargo.Id, cargo.SourceId, articulo, l.Quantity, cargo.LocationId));
            }
        }
        if (devoluciones.Count == 0)
        {
            return;
        }
        try
        {
            await stockReturn.ReturnFromCreditNoteAsync(orgId, devoluciones, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "La nota de crédito {Note} se emitió pero la devolución al inventario falló.", nota.Id);
        }
    }
}

public sealed class ListCreditNotesHandler(ICreditNoteRepository creditNotes, IInvoiceRepository invoices)
{
    public async Task<IReadOnlyList<CreditNoteDto>> HandleAsync(Guid orgId, Guid invoiceId, CancellationToken ct = default)
    {
        var notas = await creditNotes.ListByInvoiceAsync(orgId, invoiceId, ct);
        var lista = new List<CreditNoteDto>(notas.Count);
        foreach (var n in notas)
        {
            var doc = await invoices.GetFiscalDocumentAsync(orgId, n.Id, ct);
            lista.Add(CreditNoteMapper.ToDto(n, doc?.FullNumber));
        }
        return lista;
    }
}

/// <summary>
/// Paga la devolución de una nota de crédito desde el turno de caja abierto de quien la entrega:
/// un retiro de efectivo con el número de la nota. Así el arqueo cuadra y la devolución queda
/// trazada a una caja y a una persona.
/// </summary>
public sealed class RefundCreditNoteHandler(
    ICreditNoteRepository creditNotes, IInvoiceRepository invoices, ICashRepository cash, TimeProvider timeProvider)
{
    public async Task<(CreditNoteDto? Note, string? Error)> HandleAsync(
        Guid orgId, Guid creditNoteId, Guid cashierUserId, CancellationToken ct = default)
    {
        var nota = await creditNotes.GetForUpdateAsync(orgId, creditNoteId, ct);
        if (nota is null)
        {
            return (null, "La nota de crédito no existe.");
        }
        var turno = await cash.FindOpenSessionByCashierAsync(orgId, cashierUserId, ct);
        if (turno is null)
        {
            return (null, "No tenés un turno de caja abierto. Abrilo en la estación de caja.");
        }
        var doc = await invoices.GetFiscalDocumentAsync(orgId, nota.Id, ct);
        try
        {
            nota.MarkRefunded(turno.Id, timeProvider);
        }
        catch (InvalidOperationException ex)
        {
            return (null, ex.Message);
        }

        await cash.AddMovementAsync(CashMovement.Create(
            orgId, turno.Id, CashMovementKind.Withdrawal, nota.RefundDue,
            $"Devolución de la nota de crédito {doc?.FullNumber ?? nota.Id.ToString()}", cashierUserId, null), ct);
        await creditNotes.SaveAsync(ct);
        return (CreditNoteMapper.ToDto(nota, doc?.FullNumber), null);
    }
}
