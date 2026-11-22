using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Billing.Invoicing;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Lo que el paciente debe, tal como lo ve quien cobra: lo que falta facturar y lo facturado con
/// saldo, juntos. El cajero elige a quién le cobra; el sistema decide qué hay que facturar.
/// </summary>
/// <param name="OpenInvoices">Facturas emitidas con saldo.</param>
/// <param name="InvoiceBlocker">Por qué lo pendiente no se puede facturar todavía; null si se puede o no hay.</param>
/// <param name="CollectBlocker">Por qué no se puede cobrar en el acto; null si se puede.</param>
/// <param name="CashPointName">La caja del turno abierto del usuario, si tiene uno.</param>
/// <param name="BuyerName">Nombre que lleva la factura si no se cambia.</param>
/// <param name="BuyerTaxId">RTN registrado del cliente, si tiene.</param>
/// <param name="IssuerWarning">
/// Datos del emisor que el SAR exige en la factura (art. 10.1) y que faltan en la sede o su razón
/// social. Avisa, no bloquea: la factura sale igual, pero incompleta.
/// </param>
public sealed record AccountCheckoutDto(
    AccountStatementDto Statement,
    IReadOnlyList<InvoiceDto> OpenInvoices,
    decimal PendingToInvoice,
    decimal BalanceDue,
    string? InvoiceBlocker,
    string? CollectBlocker,
    string? CashPointName,
    string? BuyerName,
    string? BuyerTaxId,
    string? IssuerWarning = null)
{
    /// <summary>Todo lo que se puede cobrar hoy: lo facturable más el saldo.</summary>
    public decimal TotalDue => InvoiceBlocker is null ? PendingToInvoice + BalanceDue : BalanceDue;
}

/// <param name="Tenders">Pagos. Sin ellos se factura y queda pendiente ("facturar sin cobrar").</param>
/// <param name="InvoiceIds">Facturas con saldo a cobrar; null = todas.</param>
/// <param name="InvoicePending">Facturar también lo que falta facturar. Lo normal: sí.</param>
public sealed record AccountCheckoutInput(
    string? BuyerName,
    string? BuyerTaxId,
    IReadOnlyList<TenderLine>? Tenders,
    IReadOnlyList<Guid>? InvoiceIds = null,
    bool InvoicePending = true,
    BuyerExemption? Exemption = null);

/// <summary>
/// Lo que pasó. Si la factura salió y el cobro no, la factura queda emitida y pendiente: un número
/// fiscal consumido no se deshace.
/// </summary>
/// <param name="PaidInvoiceIds">Facturas que este cobro pagó (para imprimirlas).</param>
public sealed record AccountCheckoutResult(
    AccountCheckoutDto State,
    InvoiceDto? Invoice,
    IssueRejection? InvoiceRejected,
    ReceivePaymentResult? Payment,
    IReadOnlyList<Guid> PaidInvoiceIds);

/// <summary>
/// Cobrar una cuenta: emite la factura de lo que falte, cobra lo elegido y devuelve qué imprimir.
/// Es la acción "Cobrar" de la Caja, de la recepción del laboratorio y de la orden de servicio.
///
/// <para><b>No es facturación nueva:</b> encadena <see cref="IssueInvoiceHandler"/> y
/// <see cref="ReceivePaymentHandler"/>, que mantienen sus reglas (sede de la caja, razón social,
/// RTN sobre el umbral, correlativo). Antes valida lo que se sabe de antemano —precios y caja—
/// para no consumir un número fiscal por algo previsible.</para>
///
/// <para><b>Repetir no factura dos veces.</b> La emisión toma los cargos sin facturar con bloqueo;
/// un segundo intento no encuentra ninguno y cobra la factura que ya existe, si tiene saldo.</para>
/// </summary>
public sealed class AccountCheckoutHandler(
    IInvoiceRepository invoices,
    IFiscalRepository fiscal,
    ICashRepository cash,
    IPartyRepository parties,
    GetAccountStatementHandler statement,
    IssueInvoiceHandler issueInvoice,
    ReceivePaymentHandler receivePayment,
    ResolveActiveLocationHandler activeLocation,
    ILocationRepository locations,
    ILegalEntityRepository legalEntities,
    ILogger<AccountCheckoutHandler> logger)
{
    public async Task<AccountCheckoutDto?> GetAsync(
        Guid organizationId, Guid userId, Guid accountId, CancellationToken ct = default)
    {
        var estado = await statement.HandleAsync(organizationId, accountId, ct);
        if (estado is null)
        {
            return null;
        }

        var abiertas = new List<InvoiceDto>();
        foreach (var f in (await invoices.ListByAccountAsync(organizationId, accountId, ct))
                     .Where(f => f.Status is not (InvoiceStatus.Voided or InvoiceStatus.Draft) && f.Balance > 0))
        {
            abiertas.Add(InvoiceMapper.ToDto(f, await invoices.GetFiscalDocumentAsync(organizationId, f.Id, ct)));
        }

        var porFacturar = estado.Charges.Where(c => c.Status == nameof(ChargeStatus.Captured)).ToList();
        string? bloqueo = null;
        if (porFacturar.Where(c => c.PricePending == true).Select(c => c.Description).Distinct().ToList()
            is { Count: > 0 } sinPrecio)
        {
            bloqueo = $"{string.Join(", ", sinPrecio)} todavía no tiene precio. " +
                      "El jefe administrativo lo fija en Facturación → Catálogo de cobrables → Sin precio.";
        }

        var (cajaNombre, bloqueoCobro) = await CashStateAsync(organizationId, userId, ct);
        var parte = await parties.GetAsync(organizationId, estado.Account.PartyId, ct);
        return new AccountCheckoutDto(
            estado, abiertas,
            PendingToInvoice: porFacturar.Sum(c => c.Total),
            BalanceDue: abiertas.Sum(f => f.Balance),
            bloqueo, bloqueoCobro, cajaNombre,
            BuyerName: parte?.LegalName ?? estado.Account.PartyName,
            BuyerTaxId: parte?.TaxId,
            IssuerWarning: porFacturar.Count > 0 ? await EmisorIncompletoAsync(organizationId, userId, ct) : null);
    }

    /// <summary>
    /// Qué le falta al emisor de la sede activa para cumplir el art. 10.1 del Reglamento del SAR:
    /// dirección, teléfono y correo. <c>null</c> si está completo o si no se puede saber.
    /// </summary>
    private async Task<string?> EmisorIncompletoAsync(Guid organizationId, Guid userId, CancellationToken ct)
    {
        try
        {
            var sedeId = await activeLocation.HandleAsync(organizationId, userId, null, ct);
            var sede = await locations.GetAsync(organizationId, sedeId, ct);
            if (sede is null)
            {
                return null;
            }
            var razon = await legalEntities.GetAsync(organizationId, sede.LegalEntityId, ct);
            var faltan = new List<string>();
            if (string.IsNullOrWhiteSpace(sede.Address) && string.IsNullOrWhiteSpace(razon?.FiscalAddress))
            {
                faltan.Add("dirección");
            }
            if (string.IsNullOrWhiteSpace(sede.Phone) && string.IsNullOrWhiteSpace(razon?.Phone))
            {
                faltan.Add("teléfono");
            }
            if (string.IsNullOrWhiteSpace(razon?.Email))
            {
                faltan.Add("correo electrónico");
            }
            return faltan.Count == 0 ? null
                : $"La factura saldrá sin {string.Join(", ", faltan)} del emisor, que el SAR exige. "
                  + "El administrador los completa en la sede y su razón social.";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <exception cref="InvalidOperationException">
    /// Algo previsible impide la operación: no hay nada que cobrar, hay cargos sin precio o no hay
    /// turno abierto en una caja que pueda cobrar.
    /// </exception>
    public async Task<AccountCheckoutResult?> CheckoutAsync(
        Guid organizationId, Guid userId, Guid accountId, AccountCheckoutInput input, CancellationToken ct = default)
    {
        var antes = await GetAsync(organizationId, userId, accountId, ct);
        if (antes is null)
        {
            return null;
        }

        var facturarPendiente = input.InvoicePending
            && antes.Statement.Charges.Any(c => c.Status == nameof(ChargeStatus.Captured));
        if (facturarPendiente && antes.InvoiceBlocker is { } bloqueo)
        {
            throw new InvalidOperationException(bloqueo);
        }

        var elegidas = input.InvoiceIds is null
            ? antes.OpenInvoices.Select(f => f.Id).ToList()
            : antes.OpenInvoices.Where(f => input.InvoiceIds.Contains(f.Id)).Select(f => f.Id).ToList();

        var cobrar = input.Tenders is { Count: > 0 };
        if (cobrar && antes.CollectBlocker is { } sinCaja)
        {
            throw new InvalidOperationException(sinCaja);
        }
        if (!facturarPendiente && (!cobrar || elegidas.Count == 0))
        {
            throw new InvalidOperationException(
                antes.OpenInvoices.Count == 0 && antes.PendingToInvoice == 0
                    ? "Esta cuenta no tiene nada pendiente."
                    : "No se eligió nada para cobrar.");
        }

        InvoiceDto? factura = null;
        IssueRejection? rechazo = null;
        if (facturarPendiente)
        {
            (factura, rechazo) = await FacturarAsync(organizationId, userId, accountId, input, ct);
            if (factura is not null)
            {
                elegidas.Add(factura.Id);
            }
        }

        ReceivePaymentResult? pago = null;
        var pagadas = new List<Guid>();
        if (cobrar && rechazo is null && elegidas.Count > 0)
        {
            var turno = await cash.FindOpenSessionByCashierAsync(organizationId, userId, ct)
                ?? throw new InvalidOperationException("No hay turno de caja abierto.");
            var pagador = factura?.BillToPartyId
                ?? antes.OpenInvoices.First(f => elegidas.Contains(f.Id)).BillToPartyId;
            pago = await receivePayment.HandleAsync(new ReceivePaymentCommand(
                organizationId, turno.Id, pagador, userId, elegidas, input.Tenders!), ct);
            if (pago.Succeeded)
            {
                pagadas.AddRange(elegidas);
            }
            else
            {
                logger.LogInformation(
                    "Cuenta {Account}: cobro rechazado ({Code}).", accountId, pago.Code);
            }
        }

        var despues = (await GetAsync(organizationId, userId, accountId, ct))!;
        return new AccountCheckoutResult(despues, factura, rechazo, pago, pagadas);
    }

    private async Task<(InvoiceDto?, IssueRejection?)> FacturarAsync(
        Guid organizationId, Guid userId, Guid accountId, AccountCheckoutInput input, CancellationToken ct)
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
            UserId: userId,
            Exemption: input.Exemption), ct);
        // Otro clic ganó la emisión: no es un error, la factura ya existe y se cobra esa.
        if (r.Rejected is { Code: "sin_cargos" })
        {
            return (null, null);
        }
        return r.Succeeded ? (r.Invoice, null) : (null, r.Rejected);
    }

    /// <summary>
    /// ¿Puede cobrar acá? Un turno abierto en una caja de servicios o mixta de la sede activa. La
    /// farmacia cobra en las de farmacia; un servicio no.
    /// </summary>
    internal async Task<(string? Caja, string? Bloqueo)> CashStateAsync(
        Guid organizationId, Guid userId, CancellationToken ct)
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
            return (caja.Name, $"La caja {caja.Name} es de farmacia: los servicios se cobran en una caja de servicios o mixta.");
        }
        return (caja.Name, null);
    }
}
