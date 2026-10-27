using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Pharmacy;

public sealed record SaleLineInput(Guid StockItemId, decimal Quantity, Guid? StockLotId);

/// <summary>
/// Venta de mostrador.
/// </summary>
/// <param name="PatientFhirId">Opcional: si el comprador es paciente del expediente, la cuenta es suya.</param>
/// <param name="BuyerName">Nombre para la factura. Sin él, "Consumidor final".</param>
/// <param name="BuyerTaxId">RTN del comprador, si lo pide (obligatorio por encima del monto del SAR).</param>
/// <param name="Tenders">Cobro en el acto. Exige un turno abierto en una caja de farmacia o mixta.</param>
public sealed record OverTheCounterSaleInput(
    string IdempotencyKey,
    Guid? WarehouseId,
    string? PatientFhirId,
    string? PatientName,
    string? BuyerName,
    string? BuyerTaxId,
    string? Notes,
    IReadOnlyList<SaleLineInput> Lines,
    IReadOnlyList<TenderLine>? Tenders);

/// <summary>
/// Lo que pasó con una venta: siempre la venta; la factura y el cobro si salieron. Que la factura
/// falle (sin CAI, RTN faltante) no deshace la venta ya entregada: queda registrada y se reintenta
/// la factura o se anula la venta.
/// </summary>
public sealed record SaleResultDto(
    DispenseDto Sale,
    bool AlreadyProcessed,
    InvoiceDto? Invoice,
    IssueRejection? InvoiceRejected,
    ReceivePaymentResult? Payment);

/// <summary>Cobro de una venta ya facturada en una caja de farmacia o mixta.</summary>
public sealed record CollectSaleInput(IReadOnlyList<TenderLine> Tenders);

/// <summary>
/// Venta libre de mostrador (B5, D6, P11).
///
/// <para><b>Solo artículos de venta libre y con precio.</b> P11 limita el mostrador a lo marcado
/// "venta sin receta", y como la factura sale en el acto, un artículo "sin precio" (D1) no se
/// puede vender: se rechaza antes de tocar el inventario, con el nombre de lo que falta.</para>
///
/// <para><b>Tres pasos, una sola costura atómica.</b> (1) En una transacción: cuenta propia de la
/// venta (D2), salida FEFO y cargos. (2) La factura, con el handler de facturación existente. (3) El
/// cobro, con el handler de caja existente, en una caja de perfil Farmacia o Mixta. La factura y el
/// cobro tienen sus propias transacciones (no se pueden anidar), así que se validan de antemano las
/// reglas que se conocen —el turno, su sede (FAC-9) y su perfil— para no dejar una venta a medias
/// por algo previsible. Si aun así la factura falla, la venta queda registrada y el mostrador
/// reintenta la factura o anula la venta.</para>
/// </summary>
public sealed class OverTheCounterSaleHandler(
    IInventoryRepository repo,
    IInventoryUnitOfWork uow,
    IStockLedger ledger,
    StockOutPlanner planner,
    WarehouseResolver warehouses,
    StockItemCommandHandler items,
    IBillingCatalogRepository catalog,
    IEncounterAccountResolver accountResolver,
    IBillingAccountRepository accounts,
    IPartyRepository parties,
    CaptureChargeHandler captureCharge,
    ReverseChargeHandler reverseCharge,
    IssueInvoiceHandler issueInvoice,
    ReceivePaymentHandler receivePayment,
    ICashRepository cash,
    IFiscalRepository fiscal,
    IInvoiceRepository invoices,
    DispenseReader reader,
    ILogger<OverTheCounterSaleHandler> logger)
{
    /// <summary>Nombre de la parte con la que se abren las ventas sin paciente.</summary>
    public const string WalkInName = "Consumidor final";

    public async Task<SaleResultDto> SellAsync(
        Guid organizationId, Guid userId, OverTheCounterSaleInput input, CancellationToken ct = default)
    {
        var llave = input.IdempotencyKey?.Trim() ?? string.Empty;
        if (await repo.FindDispenseByKeyAsync(organizationId, llave, ct) is { } previa)
        {
            return await ResultadoAsync(previa, alreadyProcessed: true, null, null, ct);
        }
        if (input.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("La venta no tiene artículos.");
        }
        if (input.Lines.GroupBy(l => (l.StockItemId, l.StockLotId)).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Un artículo va una sola vez en la venta: sumá la cantidad en su línea.");
        }

        var bodega = await warehouses.ResolveAsync(organizationId, userId, input.WarehouseId, preferPharmacy: true, ct);
        var cobrar = input.Tenders is { Count: > 0 };
        await ValidarCajaAsync(organizationId, userId, bodega.LocationId, requerida: cobrar, ct);

        // ── Artículos: de venta libre, con cobrable y con precio ──
        var articulos = new Dictionary<Guid, (StockItem Item, Guid Cobrable)>();
        foreach (var l in input.Lines)
        {
            if (articulos.ContainsKey(l.StockItemId))
            {
                continue;
            }
            var item = await repo.GetItemAsync(organizationId, l.StockItemId, ct)
                ?? throw new ArgumentException("Uno de los artículos no existe.");
            if (!item.Active || !item.Sellable || !item.OverTheCounter)
            {
                throw new InvalidOperationException(
                    $"{item.Name} no es de venta libre: solo se despacha contra receta.");
            }
            var cobrableId = await items.AsegurarCobrableAsync(organizationId, item, ct)
                ?? throw new InvalidOperationException(
                    $"{item.Name} todavía no tiene su cobrable: la organización necesita su régimen fiscal configurado.");
            var cobrable = await catalog.GetItemAsync(organizationId, cobrableId, ct);
            if (cobrable is null || cobrable.PricePending)
            {
                throw new InvalidOperationException(
                    $"{item.Name} todavía no tiene precio. El jefe administrativo lo fija en Catálogo de cobrables → Sin precio.");
            }
            articulos[item.Id] = (item, cobrableId);
        }

        Guid id;
        try
        {
            id = await uow.ExecuteAsync(async t =>
            {
                var venta = PharmacyDispense.Start(
                    organizationId, PharmacyDispenseKind.OverTheCounter, llave, bodega.LocationId, bodega.Id,
                    input.PatientFhirId, input.PatientName, null, userId, input.Notes,
                    input.BuyerName, input.BuyerTaxId);
                await repo.AddDispenseAsync(venta, t);
                await uow.SaveChangesAsync(t);

                venta.AssignAccount(await CuentaAsync(organizationId, venta, t));

                var patas = new List<StockPosting>();
                foreach (var l in input.Lines)
                {
                    var (item, _) = articulos[l.StockItemId];
                    var linea = venta.AddLine(item.Id, item.Name, l.Quantity);
                    foreach (var a in await planner.PlanAsync(bodega, item, l.Quantity, l.StockLotId, allowUnusable: false, t))
                    {
                        patas.Add(new StockPosting(StockMovementType.Sale, item.Id, a.StockLotId, bodega.Id, -1, a.Quantity, linea.Id));
                    }
                }
                var movs = await ledger.PostAsync(organizationId, venta.Id, StockSources.PharmacyDispense, venta.Id, userId, patas, t);
                foreach (var linea in venta.Lines)
                {
                    foreach (var m in movs.Where(m => m.SourceLineId == linea.Id))
                    {
                        linea.AddLot(m.StockLotId, m.Quantity, m.UnitCost);
                    }
                }
                await uow.SaveChangesAsync(t);

                foreach (var linea in venta.Lines)
                {
                    var cargo = await captureCharge.HandleAsync(new CaptureChargeCommand(
                        organizationId, venta.AccountId, articulos[linea.StockItemId].Cobrable,
                        ChargeSourceKind.PharmacyDispense, linea.Id.ToString(), Quantity: linea.Quantity,
                        RequestedByUserId: userId, LocationId: bodega.LocationId), t);
                    if (!cargo.Succeeded)
                    {
                        throw new InvalidOperationException($"No se pudo cargar {linea.Description}: {cargo.Rejected}");
                    }
                    linea.LinkCharge(cargo.Charge!.Id);
                }
                return venta.Id;
            }, ct);
        }
        catch (Exception ex) when (uow.IsUniqueViolation(ex))
        {
            var ganadora = await repo.FindDispenseByKeyAsync(organizationId, llave, ct);
            if (ganadora is null)
            {
                throw;
            }
            return await ResultadoAsync(ganadora, alreadyProcessed: true, null, null, ct);
        }

        var hecha = (await repo.GetDispenseAsync(organizationId, id, ct))!;
        var (factura, rechazo) = await FacturarAsync(organizationId, userId, hecha, ct);
        ReceivePaymentResult? pago = null;
        if (factura is not null && cobrar)
        {
            pago = await CobrarAsync(organizationId, userId, factura, input.Tenders!, ct);
        }
        return await ResultadoAsync(hecha, alreadyProcessed: false, rechazo, pago, ct, factura);
    }

    /// <summary>Reintenta la factura de una venta que quedó sin facturar.</summary>
    public async Task<SaleResultDto?> InvoiceAsync(
        Guid organizationId, Guid userId, Guid saleId, CancellationToken ct = default)
    {
        var venta = await VentaAsync(organizationId, userId, saleId, ct);
        if (venta is null)
        {
            return null;
        }
        if (venta.Status == PharmacyDispenseStatus.Cancelled)
        {
            throw new InvalidOperationException("La venta está anulada.");
        }
        if (venta.InvoiceId is not null)
        {
            return await ResultadoAsync(venta, alreadyProcessed: true, null, null, ct);
        }
        await ValidarCajaAsync(organizationId, userId, venta.LocationId, requerida: false, ct);
        var (factura, rechazo) = await FacturarAsync(organizationId, userId, venta, ct);
        return await ResultadoAsync(venta, alreadyProcessed: false, rechazo, null, ct, factura);
    }

    /// <summary>Cobra una venta facturada en el turno del usuario, en una caja de farmacia o mixta.</summary>
    public async Task<SaleResultDto?> CollectAsync(
        Guid organizationId, Guid userId, Guid saleId, CollectSaleInput input, CancellationToken ct = default)
    {
        var venta = await VentaAsync(organizationId, userId, saleId, ct);
        if (venta is null)
        {
            return null;
        }
        if (venta.InvoiceId is not { } facturaId)
        {
            throw new InvalidOperationException("La venta todavía no tiene factura: emitila antes de cobrar.");
        }
        await ValidarCajaAsync(organizationId, userId, venta.LocationId, requerida: true, ct);
        var factura = await invoices.GetAsync(organizationId, facturaId, ct)
            ?? throw new InvalidOperationException("La factura de la venta no existe.");
        var pago = await CobrarAsync(organizationId, userId, InvoiceMapper.ToDto(factura, null), input.Tenders, ct);
        return await ResultadoAsync(venta, alreadyProcessed: false, null, pago, ct);
    }

    /// <summary>
    /// Anula una venta que no llegó a facturarse: la mercadería vuelve a su lote y los cargos se
    /// reversan, juntos. Lo facturado no se anula aquí: se corrige con nota de crédito (D10).
    /// </summary>
    public async Task<SaleResultDto?> CancelAsync(
        Guid organizationId, Guid userId, Guid saleId, string reason, CancellationToken ct = default)
    {
        var venta = await VentaAsync(organizationId, userId, saleId, ct);
        if (venta is null)
        {
            return null;
        }
        await uow.ExecuteAsync(async t =>
        {
            venta.Cancel(userId, reason);
            var patas = venta.Lines.SelectMany(l => l.Lots.Select(x => new StockPosting(
                StockMovementType.SaleCancellation, l.StockItemId, x.StockLotId, venta.WarehouseId, +1, x.Quantity, l.Id,
                UnitCost: x.UnitCost, Notes: reason))).ToList();
            await ledger.PostAsync(organizationId, Guid.CreateVersion7(), StockSources.PharmacyDispense, venta.Id, userId, patas, t);
            foreach (var linea in venta.Lines.Where(l => l.ChargeId is not null))
            {
                await reverseCharge.HandleAsync(organizationId, linea.ChargeId!.Value, $"Venta anulada: {reason}", t);
            }
            await uow.SaveChangesAsync(t);
            return true;
        }, ct);
        return await ResultadoAsync(venta, alreadyProcessed: false, null, null, ct);
    }

    private async Task<PharmacyDispense?> VentaAsync(Guid organizationId, Guid userId, Guid saleId, CancellationToken ct)
    {
        var venta = await repo.GetDispenseAsync(organizationId, saleId, ct);
        if (venta is null || venta.Kind != PharmacyDispenseKind.OverTheCounter)
        {
            return null;
        }
        // Solo quien opera en la sede de la venta la factura, la cobra o la anula.
        await warehouses.ResolveAsync(organizationId, userId, venta.WarehouseId, preferPharmacy: true, ct);
        return venta;
    }

    /// <summary>
    /// Reglas de caja que se conocen antes de vender (FAC-9): quien tiene un turno abierto factura y
    /// cobra con la sede de su caja, y el mostrador cobra solo en una caja de farmacia o mixta.
    /// </summary>
    private async Task ValidarCajaAsync(Guid organizationId, Guid userId, Guid locationId, bool requerida, CancellationToken ct)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(organizationId, userId, ct);
        if (turno is null)
        {
            if (requerida)
            {
                throw new InvalidOperationException(
                    "Para cobrar en el mostrador abrí un turno en una caja de farmacia o mixta (Facturación → Caja).");
            }
            return;
        }
        var caja = await fiscal.GetCashPointAsync(organizationId, turno.CashPointId, ct)
            ?? throw new InvalidOperationException("La caja del turno abierto no existe.");
        if (caja.LocationId != locationId)
        {
            throw new InvalidOperationException(
                $"Tu turno está abierto en la caja {caja.Name}, de otra sede. Cambiá la sede activa a esa sede o cerrá el turno.");
        }
        if (requerida && caja.Profile is not (CashPointProfile.Pharmacy or CashPointProfile.Mixed))
        {
            throw new InvalidOperationException(
                $"La caja {caja.Name} es de servicios: el mostrador cobra en una caja de farmacia o mixta.");
        }
    }

    /// <summary>
    /// La cuenta propia de la venta (D2): la del paciente si se eligió uno, o la de "Consumidor
    /// final". Idempotente por la venta (origen <c>PharmacySale</c> + id de la venta).
    /// </summary>
    private async Task<Guid> CuentaAsync(Guid organizationId, PharmacyDispense venta, CancellationToken ct)
    {
        var referencia = venta.Id.ToString();
        if (venta.PatientFhirId is { } paciente)
        {
            return await accountResolver.GetOrOpenStandaloneAsync(
                    organizationId, BillingAccountOrigin.PharmacySale, referencia, paciente, venta.PatientName, ct)
                ?? throw new InvalidOperationException("No se pudo abrir la cuenta de la venta.");
        }

        // Sin paciente no hay a quién colgarle la cuenta en el resolutor compartido (pide paciente):
        // se abre con la parte "Consumidor final" de la organización, una sola para todas las ventas.
        var parte = await ConsumidorFinalAsync(organizationId, ct);
        if (await accounts.FindByOriginAsync(organizationId, BillingAccountOrigin.PharmacySale, referencia, ct) is { } ya)
        {
            return ya.Id;
        }
        var cuenta = BillingAccount.Open(
            organizationId, parte, BillingAccountKind.Episode, origin: BillingAccountOrigin.PharmacySale, originRef: referencia);
        if (await accounts.TryAddAccountAsync(cuenta, ct))
        {
            return cuenta.Id;
        }
        return (await accounts.FindByOriginAsync(organizationId, BillingAccountOrigin.PharmacySale, referencia, ct))?.Id
            ?? throw new InvalidOperationException("No se pudo abrir la cuenta de la venta.");
    }

    private async Task<Guid> ConsumidorFinalAsync(Guid organizationId, CancellationToken ct)
    {
        var ajustes = await repo.GetSettingsAsync(organizationId, ct);
        if (ajustes.WalkInPartyId is { } existente && await parties.GetAsync(organizationId, existente, ct) is not null)
        {
            return existente;
        }
        var parte = Party.Create(organizationId, PartyKind.WalkInCustomer, WalkInName);
        await parties.AddAsync(parte, ct);
        ajustes.SetWalkInParty(parte.Id);
        await repo.SaveSettingsAsync(ajustes, ct);
        await uow.SaveChangesAsync(ct);
        return parte.Id;
    }

    private async Task<(InvoiceDto? Factura, IssueRejection? Rechazo)> FacturarAsync(
        Guid organizationId, Guid userId, PharmacyDispense venta, CancellationToken ct)
    {
        var tipo = (await fiscal.ListDocumentTypesAsync(organizationId, ct)).FirstOrDefault(t => t.Code == "factura");
        if (tipo is null)
        {
            return (null, new IssueRejection("sin_regimen", "La organización no tiene régimen fiscal configurado."));
        }
        var resultado = await issueInvoice.HandleAsync(new IssueInvoiceCommand(
            organizationId, venta.AccountId, venta.LocationId, tipo.Id,
            BuyerTaxId: venta.BuyerTaxId,
            BuyerName: venta.BuyerName ?? (venta.PatientFhirId is null ? WalkInName : null),
            UserId: userId), ct);
        if (!resultado.Succeeded)
        {
            logger.LogInformation(
                "Venta {SaleId} registrada sin factura: {Code} — {Message}",
                venta.Id, resultado.Rejected!.Code, resultado.Rejected.Message);
            return (null, resultado.Rejected);
        }
        var fresca = (await repo.GetDispenseAsync(organizationId, venta.Id, ct))!;
        fresca.MarkInvoiced(resultado.Invoice!.Id);
        await uow.SaveChangesAsync(ct);
        venta.MarkInvoiced(resultado.Invoice.Id);
        return (resultado.Invoice, null);
    }

    private async Task<ReceivePaymentResult> CobrarAsync(
        Guid organizationId, Guid userId, InvoiceDto factura, IReadOnlyList<TenderLine> tenders, CancellationToken ct)
    {
        var turno = await cash.FindOpenSessionByCashierAsync(organizationId, userId, ct)
            ?? throw new InvalidOperationException("No hay turno de caja abierto.");
        return await receivePayment.HandleAsync(new ReceivePaymentCommand(
            organizationId, turno.Id, factura.BillToPartyId, userId, [factura.Id], tenders), ct);
    }

    private async Task<SaleResultDto> ResultadoAsync(
        PharmacyDispense venta, bool alreadyProcessed, IssueRejection? rechazo, ReceivePaymentResult? pago,
        CancellationToken ct, InvoiceDto? factura = null)
    {
        if (factura is null && venta.InvoiceId is { } fid)
        {
            var f = await invoices.GetAsync(venta.OrganizationId, fid, ct);
            factura = f is null ? null : InvoiceMapper.ToDto(f, await invoices.GetFiscalDocumentAsync(venta.OrganizationId, fid, ct));
        }
        return new SaleResultDto(await reader.ToDtoAsync(venta, ct), alreadyProcessed, factura, rechazo, pago);
    }
}
