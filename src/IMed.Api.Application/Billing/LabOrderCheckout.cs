using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// El cobro de una orden externa tal como lo ve la recepción del laboratorio.
/// </summary>
/// <param name="FromConsultation">
/// La orden salió de una consulta: su cargo está en la cuenta de la consulta, junto con la consulta y
/// los medicamentos, y se cobra en caja. La recepción no la factura.
/// </param>
/// <param name="InvoiceBlocker">Por qué no se puede facturar todavía; null si se puede.</param>
/// <param name="CollectBlocker">Por qué no se puede cobrar en el acto; null si se puede.</param>
/// <param name="CashPointName">La caja del turno abierto del usuario, si tiene uno.</param>
public sealed record LabCheckoutDto(
    string Requisition,
    bool FromConsultation,
    AccountStatementDto? Statement,
    IReadOnlyList<InvoiceDto> Invoices,
    decimal PendingToInvoice,
    decimal BalanceDue,
    string? InvoiceBlocker,
    string? CollectBlocker,
    string? CashPointName);

/// <param name="BuyerName">Nombre en la factura; sin él, el del paciente.</param>
/// <param name="BuyerTaxId">RTN del comprador, si lo pide.</param>
/// <param name="Tenders">Cobro en el acto. Sin él la factura queda pendiente en caja.</param>
public sealed record LabCheckoutInput(
    string? BuyerName,
    string? BuyerTaxId,
    IReadOnlyList<TenderLine>? Tenders);

/// <summary>
/// Lo que pasó: el estado final siempre; la factura y el cobro si salieron. Si la factura salió y
/// el cobro no, la factura queda emitida y pendiente en caja: no se deshace.
/// </summary>
public sealed record LabCheckoutResult(
    LabCheckoutDto State,
    InvoiceDto? Invoice,
    IssueRejection? InvoiceRejected,
    ReceivePaymentResult? Payment);

/// <summary>
/// Facturar y cobrar una orden externa desde la recepción del laboratorio (para el laboratorio
/// chico, donde quien recibe al paciente también cobra).
///
/// <para><b>No es facturación nueva:</b> encadena la emisión (<see cref="IssueInvoiceHandler"/>) y
/// el cobro (<see cref="ReceivePaymentHandler"/>) existentes, como la venta libre de farmacia. Antes
/// valida lo que se sabe de antemano —precios, turno, sede y perfil de la caja— para no dejar una
/// factura emitida por algo previsible.</para>
///
/// <para><b>Solo órdenes externas.</b> Una orden de consulta va a la cuenta de la consulta, y su
/// factura se llevaría también la consulta y los medicamentos: eso se cobra en caja.</para>
///
/// <para><b>Repetir no factura dos veces.</b> La emisión toma los cargos sin facturar con bloqueo;
/// un segundo intento no encuentra ninguno y cobra la factura que ya existe, si tiene saldo.</para>
/// </summary>
public sealed class LabOrderCheckoutHandler(
    IHapiFhirClient hapi,
    IBillingAccountRepository accounts,
    IInvoiceRepository invoices,
    IFiscalRepository fiscal,
    ICashRepository cash,
    GetAccountStatementHandler statement,
    IssueInvoiceHandler issueInvoice,
    ReceivePaymentHandler receivePayment,
    ResolveActiveLocationHandler activeLocation,
    ILogger<LabOrderCheckoutHandler> logger)
{
    public async Task<LabCheckoutDto?> GetAsync(
        Guid organizationId, Guid userId, string requisition, CancellationToken ct = default)
    {
        var req = requisition?.Trim() ?? string.Empty;
        var ordenes = await hapi.SearchLabOrdersByRequisitionAsync(req, LabRouting.PartitionOf(organizationId), ct);
        if (ordenes.Count == 0)
        {
            return null;
        }
        if (ordenes.Any(o => !string.IsNullOrWhiteSpace(o.EncounterFhirId)))
        {
            return new LabCheckoutDto(req, true, null, [], 0m, 0m,
                "Esta orden viene de una consulta: se cobra en caja, con la cuenta de la consulta.", null, null);
        }

        var cuenta = await accounts.FindByOriginAsync(organizationId, BillingAccountOrigin.LabOrder, req, ct);
        var estado = cuenta is null ? null : await statement.HandleAsync(organizationId, cuenta.Id, ct);
        var facturas = new List<InvoiceDto>();
        if (cuenta is not null)
        {
            foreach (var f in (await invoices.ListByAccountAsync(organizationId, cuenta.Id, ct))
                         .Where(f => f.Status != InvoiceStatus.Voided && f.Status != InvoiceStatus.Draft))
            {
                // Con su número fiscal: es lo que la recepción muestra e imprime.
                facturas.Add(InvoiceMapper.ToDto(f, await invoices.GetFiscalDocumentAsync(organizationId, f.Id, ct)));
            }
        }

        var porFacturar = estado?.Charges.Where(c => c.Status == nameof(ChargeStatus.Captured)).ToList() ?? [];
        var saldo = facturas.Sum(f => f.Balance);

        string? bloqueoFactura = null;
        if (cuenta is null || estado is null || (porFacturar.Count == 0 && facturas.Count == 0))
        {
            bloqueoFactura = "La orden no tiene cargos: admitila primero, o sus exámenes no están en el catálogo de cobrables.";
        }
        else if (porFacturar.Where(c => c.PricePending == true).Select(c => c.Description).Distinct().ToList() is { Count: > 0 } sinPrecio)
        {
            bloqueoFactura = $"{string.Join(", ", sinPrecio)} todavía no tiene precio. " +
                             "El jefe administrativo lo fija en Facturación → Catálogo de cobrables → Sin precio.";
        }
        else if (porFacturar.Count == 0 && saldo <= 0)
        {
            bloqueoFactura = "La orden ya está facturada y pagada.";
        }

        var (cajaNombre, bloqueoCobro) = await CajaAsync(organizationId, userId, ct);
        return new LabCheckoutDto(
            req, false, estado, facturas,
            PendingToInvoice: porFacturar.Sum(c => c.Total),
            BalanceDue: saldo,
            bloqueoFactura, bloqueoCobro, cajaNombre);
    }

    public async Task<LabCheckoutResult?> CheckoutAsync(
        Guid organizationId, Guid userId, string requisition, LabCheckoutInput input, CancellationToken ct = default)
    {
        var antes = await GetAsync(organizationId, userId, requisition, ct);
        if (antes is null)
        {
            return null;
        }
        if (antes.FromConsultation)
        {
            throw new InvalidOperationException(antes.InvoiceBlocker);
        }
        // "Ya facturada y pagada" no es un error: es lo que pasa al apretar dos veces.
        if (antes.InvoiceBlocker is { } bloqueo && !(antes.PendingToInvoice == 0 && antes.BalanceDue <= 0 && antes.Invoices.Count > 0))
        {
            throw new InvalidOperationException(bloqueo);
        }
        var cobrar = input.Tenders is { Count: > 0 };
        if (cobrar && antes.CollectBlocker is { } sinCaja)
        {
            throw new InvalidOperationException(sinCaja);
        }

        var cuentaId = antes.Statement!.Account.Id;
        InvoiceDto? factura = null;
        IssueRejection? rechazo = null;
        if (antes.Statement.Charges.Any(c => c.Status == nameof(ChargeStatus.Captured)))
        {
            (factura, rechazo) = await FacturarAsync(organizationId, userId, cuentaId, input, ct);
        }

        ReceivePaymentResult? pago = null;
        if (cobrar && rechazo is null)
        {
            var porCobrar = (await invoices.ListByAccountAsync(organizationId, cuentaId, ct))
                .Where(f => f.Status != InvoiceStatus.Voided && f.Status != InvoiceStatus.Draft && f.Balance > 0)
                .ToList();
            if (porCobrar.Count > 0)
            {
                var turno = await cash.FindOpenSessionByCashierAsync(organizationId, userId, ct)
                    ?? throw new InvalidOperationException("No hay turno de caja abierto.");
                pago = await receivePayment.HandleAsync(new ReceivePaymentCommand(
                    organizationId, turno.Id, porCobrar[0].BillToPartyId, userId,
                    porCobrar.Select(f => f.Id).ToList(), input.Tenders!), ct);
                if (!pago.Succeeded)
                {
                    logger.LogInformation(
                        "Orden {Requisition}: facturada, cobro rechazado ({Code}).", antes.Requisition, pago.Code);
                }
            }
        }

        var despues = (await GetAsync(organizationId, userId, requisition, ct))!;
        return new LabCheckoutResult(despues, factura, rechazo, pago);
    }

    private async Task<(InvoiceDto?, IssueRejection?)> FacturarAsync(
        Guid organizationId, Guid userId, Guid accountId, LabCheckoutInput input, CancellationToken ct)
    {
        var tipo = (await fiscal.ListDocumentTypesAsync(organizationId, ct)).FirstOrDefault(t => t.Code == "factura");
        if (tipo is null)
        {
            return (null, new IssueRejection("sin_regimen", "La organización no tiene régimen fiscal configurado."));
        }
        // La sede de emisión es la activa del usuario (FAC-9, D4); la emisión exige además que sea
        // la de su caja si tiene turno.
        var sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        var r = await issueInvoice.HandleAsync(new IssueInvoiceCommand(
            organizationId, accountId, sede, tipo.Id,
            BuyerTaxId: string.IsNullOrWhiteSpace(input.BuyerTaxId) ? null : input.BuyerTaxId.Trim(),
            BuyerName: string.IsNullOrWhiteSpace(input.BuyerName) ? null : input.BuyerName.Trim(),
            UserId: userId), ct);
        // Otro clic ganó la emisión: no es un error, la factura ya existe y se cobra esa.
        if (r.Rejected is { Code: "sin_cargos" })
        {
            return (null, null);
        }
        return r.Succeeded ? (r.Invoice, null) : (null, r.Rejected);
    }

    /// <summary>
    /// ¿Puede cobrar acá? Un turno abierto en una caja de servicios o mixta de la sede activa. La
    /// farmacia cobra en las de farmacia; un examen es un servicio.
    /// </summary>
    private async Task<(string? Caja, string? Bloqueo)> CajaAsync(Guid organizationId, Guid userId, CancellationToken ct)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(organizationId, userId, ct);
        if (turno is null)
        {
            return (null, "Para cobrar abrí un turno en una caja de servicios o mixta (Facturación → Caja).");
        }
        var caja = await fiscal.GetCashPointAsync(organizationId, turno.CashPointId, ct);
        if (caja is null)
        {
            return (null, "La caja del turno abierto no existe.");
        }
        var sede = await activeLocation.HandleAsync(organizationId, userId, null, ct);
        if (caja.LocationId != sede)
        {
            return (caja.Name, $"Tu turno está abierto en la caja {caja.Name}, de otra sede. Cambiá la sede activa a esa sede o cerrá el turno.");
        }
        if (caja.Profile is not (CashPointProfile.Services or CashPointProfile.Mixed))
        {
            return (caja.Name, $"La caja {caja.Name} es de farmacia: los exámenes se cobran en una caja de servicios o mixta.");
        }
        return (caja.Name, null);
    }
}
