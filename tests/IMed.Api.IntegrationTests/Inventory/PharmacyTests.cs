using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Inventory;
using IMed.Api.Application.Pharmacy;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Inventory;
using IMed.Api.Domain.Pharmacy;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Inventory;

/// <summary>
/// Farmacia (plan de salida a producción, B4–B6): despacho de receta con su cargo en la cuenta de
/// la consulta, en una sola transacción e idempotente; venta libre con factura y cobro en caja de
/// farmacia; y la devolución al inventario por nota de crédito.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PharmacyTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        fixture.Hapi.EnablePharmacy();
        return fixture.ResetStateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    private sealed record Receta(InventoryScenario E, StockItemDto Item, string PatientId, string EncounterId, string MrId);

    /// <summary>Una consulta en la sede con una receta de amoxicilina, y amoxicilina en la bodega.</summary>
    private async Task<Receta> ArrangeAsync(decimal stock = 20, decimal? prescribed = 21, bool conPrecio = false)
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("AMOX", "Amoxicilina 500 mg", "J01CA04", taxCodeId: e.ExentoId));
        if (stock > 0)
        {
            await e.ReceiveAsync(fixture, item.Id, "L-TARDE", InventoryScenario.InDays(300), stock / 2, 2m);
            await e.ReceiveAsync(fixture, item.Id, "L-PRONTO", InventoryScenario.InDays(60), stock / 2, 2m);
        }
        if (conPrecio)
        {
            await e.SetPriceAsync(fixture, item.BillableItemId!.Value, 5m);
        }
        var paciente = $"pat-{Guid.NewGuid():N}"[..20];
        var encuentro = fixture.Hapi.SeedEncounterAt(e.LocationId, paciente);
        var mr = fixture.Hapi.SeedMedicationRequest(paciente, encuentro, "J01CA04", "Amoxicilina", prescribed);
        return new Receta(e, item, paciente, encuentro, mr);
    }

    private static DispensePrescriptionInput Despacho(Receta r, decimal qty, bool completa = true, Guid? itemId = null,
        string? sustitucion = null, string? key = null) =>
        new(key ?? Guid.NewGuid().ToString(), null, null,
            [new DispenseLineInput(r.MrId, itemId ?? r.Item.Id, qty, null, completa, sustitucion)]);

    // ── Cola ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cola_muestraLasRecetasDeLaSede_conElArticuloPropuesto()
    {
        var r = await ArrangeAsync();
        // Una receta de otra sede no aparece.
        var otraSede = Guid.NewGuid();
        var encOtra = fixture.Hapi.SeedEncounterAt(otraSede, r.PatientId);
        fixture.Hapi.SeedMedicationRequest(r.PatientId, encOtra, "J01CA04", "Amoxicilina", 7);

        using var scope = fixture.Factory.Services.CreateScope();
        var cola = await Get<PrescriptionQueueHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, null, 7);

        cola.Prescriptions.Should().ContainSingle();
        var med = cola.Prescriptions[0].Medications.Should().ContainSingle().Subject;
        med.MedicationRequestId.Should().Be(r.MrId);
        med.PrescribedQuantity.Should().Be(21m);
        med.ProposedStockItemId.Should().Be(r.Item.Id);
        med.Candidates.Single().Usable.Should().Be(20m);
        med.Candidates.Single().PricePending.Should().BeTrue();
    }

    // ── Despacho (B4) ────────────────────────────────────────────────────

    [Fact]
    public async Task Despacho_bajaElLoteQueVenceAntes_yCargaALaCuentaDeLaConsulta()
    {
        var r = await ArrangeAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var res = await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 14));

        res.AlreadyProcessed.Should().BeFalse();
        var linea = res.Dispense.Lines.Should().ContainSingle().Subject;
        linea.Lots.Select(l => (l.LotCode, l.Quantity)).Should().Equal(("L-PRONTO", 10m), ("L-TARDE", 4m));
        linea.ChargeId.Should().NotBeNull();
        linea.PricePending.Should().BeTrue("D1: el despacho no espera al precio; la factura sí");

        // La cuenta es la de la consulta (D2).
        var cuenta = await Get<IEncounterAccountResolver>(scope)
            .GetOrOpenForEncounterAsync(r.E.OrgId, r.EncounterId, r.PatientId, null);
        res.Dispense.AccountId.Should().Be(cuenta!.Value);

        var db = Get<AppDbContext>(scope);
        var cargo = await db.BillingCharges.AsNoTracking().SingleAsync(c => c.Id == linea.ChargeId);
        cargo.SourceKind.Should().Be(ChargeSourceKind.PharmacyDispense);
        cargo.SourceId.Should().Be(linea.Id.ToString());
        cargo.Quantity.Should().Be(14m);
        cargo.LocationId.Should().Be(r.E.LocationId);
        cargo.SourceFhirRef.Should().Be($"MedicationRequest/{r.MrId}");

        (await db.StockBalances.Where(b => b.StockItemId == r.Item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(6m);

        // Atendida: sale de la cola.
        var cola = await Get<PrescriptionQueueHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, null, 7);
        cola.Prescriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task DespachoParcial_dejaLaRecetaEnLaCola_conLoYaEntregado()
    {
        var r = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 10, completa: false));

        var cola = await Get<PrescriptionQueueHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, null, 7);
        cola.Prescriptions.Single().Medications.Single().AlreadyDispensed.Should().Be(10m);

        // El resto, otro día: completa.
        await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 10));
        (await Get<PrescriptionQueueHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, null, 7)).Prescriptions.Should().BeEmpty();

        var otra = () => Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 1));
        await otra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ya se despachó por completo*");
    }

    [Fact]
    public async Task Sustitucion_exigeConstancia_yQuedaRegistrada()
    {
        var r = await ArrangeAsync();
        var otro = await r.E.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("AMOXCLAV", "Amoxicilina + clavulánico", "J01CR02", taxCodeId: r.E.ExentoId));
        await r.E.ReceiveAsync(fixture, otro.Id, "L-X", InventoryScenario.InDays(100), 10, 4m);

        using var scope = fixture.Factory.Services.CreateScope();
        var sinMotivo = () => Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 5, itemId: otro.Id));
        await sinMotivo.Should().ThrowAsync<ArgumentException>().WithMessage("*motivo de la sustitución*");

        var res = await Get<DispensePrescriptionHandler>(scope).HandleAsync(
            r.E.OrgId, r.E.UserId, Despacho(r, 5, itemId: otro.Id, sustitucion: "Sin existencia del recetado; indicado por el médico"));
        res.Dispense.Lines.Single().SubstitutionReason.Should().StartWith("Sin existencia");
        var cargo = await Get<AppDbContext>(scope).BillingCharges.AsNoTracking().SingleAsync(c => c.Id == res.Dispense.Lines[0].ChargeId);
        cargo.Reason.Should().Contain("Sustitución");
    }

    [Fact]
    public async Task Despacho_esIdempotente_porLaLlave_aunqueLleguenDosALaVez()
    {
        var r = await ArrangeAsync();
        var input = Despacho(r, 3, key: "pantalla-123");

        var tareas = Enumerable.Range(0, 3).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, input);
        });
        var resultados = await Task.WhenAll(tareas);

        resultados.Select(x => x.Dispense.Id).Distinct().Should().ContainSingle();
        resultados.Count(x => !x.AlreadyProcessed).Should().Be(1);

        using var verify = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(verify);
        (await db.PharmacyDispenses.CountAsync(d => d.OrganizationId == r.E.OrgId)).Should().Be(1);
        (await db.BillingCharges.CountAsync(c => c.OrganizationId == r.E.OrgId && c.SourceKind == ChargeSourceKind.PharmacyDispense))
            .Should().Be(1);
        (await db.StockBalances.Where(b => b.StockItemId == r.Item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(17m);
    }

    [Fact]
    public async Task DosDespachosALaVez_deLaUltimaUnidad_unoFallaLimpio_sinCargoNiCabecera()
    {
        var r = await ArrangeAsync(stock: 2, prescribed: 1);
        var mr2 = fixture.Hapi.SeedMedicationRequest(r.PatientId, r.EncounterId, "J01CA04", "Amoxicilina", 1);
        // Queda una sola unidad.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Get<StockMovementHandlers>(scope).ConsumeAsync(r.E.OrgId, r.E.UserId,
                new ConsumptionInput(null, "Muestra", null, [new StockOutLineInput(r.Item.Id, 1, null)]));
        }

        var entradas = new[] { r.MrId, mr2 }.Select(mr => new DispensePrescriptionInput(
            Guid.NewGuid().ToString(), null, null, [new DispenseLineInput(mr, r.Item.Id, 1, null, true, null)]));
        var tareas = entradas.Select(async input =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            try
            {
                await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, input);
                return "ok";
            }
            catch (InsufficientStockException ex)
            {
                return ex.Message;
            }
        });
        var resultados = await Task.WhenAll(tareas);

        resultados.Count(x => x == "ok").Should().Be(1);
        resultados.Single(x => x != "ok").Should().Contain("No alcanza");

        using var verify = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(verify);
        (await db.PharmacyDispenses.CountAsync(d => d.OrganizationId == r.E.OrgId)).Should().Be(1, "el perdedor no deja cabecera");
        (await db.BillingCharges.CountAsync(c => c.OrganizationId == r.E.OrgId && c.SourceKind == ChargeSourceKind.PharmacyDispense))
            .Should().Be(1, "ni cargo");
        (await db.StockBalances.Where(b => b.StockItemId == r.Item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(0m);
    }

    [Fact]
    public async Task Despacho_conLaCuentaDeLaConsultaCerrada_abreSuPropiaCuenta()
    {
        var r = await ArrangeAsync(conPrecio: true);
        using var scope = fixture.Factory.Services.CreateScope();
        // La cuenta de la consulta ya se cerró antes de pasar por farmacia: no acepta cargos.
        var cuenta = (await Get<IEncounterAccountResolver>(scope).GetOrOpenForEncounterAsync(r.E.OrgId, r.EncounterId, r.PatientId, "Ana"))!.Value;
        (await Get<CloseAccountHandler>(scope).HandleAsync(r.E.OrgId, cuenta)).Should().BeTrue();

        var res = await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 2));
        res.Dispense.AccountId.Should().NotBe(cuenta);
        var propia = await Get<AppDbContext>(scope).BillingAccounts.AsNoTracking().SingleAsync(a => a.Id == res.Dispense.AccountId);
        propia.Origin.Should().Be(BillingAccountOrigin.PharmacySale);
        propia.OriginRef.Should().Be(res.Dispense.Id.ToString());
    }

    // ── Venta libre (B5) ─────────────────────────────────────────────────

    private async Task<(InventoryScenario E, StockItemDto Item, Guid CashPointId)> ArrangeVentaAsync(string perfil = "Pharmacy")
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("ACET", "Acetaminofén 500 mg", "N02BE01", otc: true, taxCodeId: e.ExentoId));
        await e.ReceiveAsync(fixture, item.Id, "L-1", InventoryScenario.InDays(200), 50, 0.5m);
        await e.SetPriceAsync(fixture, item.BillableItemId!.Value, 2m);

        using var scope = fixture.Factory.Services.CreateScope();
        var caja = await Get<CreateCashPointHandler>(scope).HandleAsync(new CreateCashPointCommand(
            e.OrgId, e.LocationId, $"CJ{Guid.NewGuid():N}"[..8], "Caja farmacia", "002", perfil, false, null));
        var turno = await Get<OpenCashSessionHandler>(scope).HandleAsync(e.OrgId, caja.Id, e.UserId, 100m);
        turno.Session.Should().NotBeNull(turno.Message);
        return (e, item, caja.Id);
    }

    [Fact]
    public async Task VentaLibre_saleFacturaYSeCobra_enUnaCajaDeFarmacia()
    {
        var (e, item, _) = await ArrangeVentaAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var r = await Get<OverTheCounterSaleHandler>(scope).SellAsync(e.OrgId, e.UserId, new OverTheCounterSaleInput(
            "venta-1", null, null, null, null, null, null,
            [new SaleLineInput(item.Id, 10, null)],
            [new TenderLine("Cash", 50m, null)]));

        r.Sale.Kind.Should().Be("OverTheCounter");
        r.Invoice.Should().NotBeNull(r.InvoiceRejected?.Message);
        r.Invoice!.GrandTotal.Should().Be(20m);
        r.Invoice.FiscalDocument!.BuyerName.Should().Be(OverTheCounterSaleHandler.WalkInName);
        r.Payment!.Succeeded.Should().BeTrue(r.Payment.Message);
        r.Payment.ChangeDue.Should().Be(30m);
        r.Sale.InvoiceId.Should().Be(r.Invoice.Id);

        var db = Get<AppDbContext>(scope);
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(40m);
        (await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == r.Invoice.Id)).Balance.Should().Be(0m);
        var cuenta = await db.BillingAccounts.AsNoTracking().SingleAsync(a => a.Id == r.Sale.AccountId);
        cuenta.Origin.Should().Be(BillingAccountOrigin.PharmacySale);

        // Reintento con la misma llave: la misma venta, sin otra salida ni otra factura.
        var otra = await Get<OverTheCounterSaleHandler>(scope).SellAsync(e.OrgId, e.UserId, new OverTheCounterSaleInput(
            "venta-1", null, null, null, null, null, null, [new SaleLineInput(item.Id, 10, null)], null));
        otra.AlreadyProcessed.Should().BeTrue();
        otra.Sale.Id.Should().Be(r.Sale.Id);
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(40m);
    }

    [Fact]
    public async Task VentaLibre_rechazaAntesDeTocarElInventario_loQueNoSePuedeVender()
    {
        var (e, item, _) = await ArrangeVentaAsync(perfil: "Services");
        var receta = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("CIPRO", "Ciprofloxacino", "J01MA02", otc: false, taxCodeId: e.ExentoId));
        var sinPrecio = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("VITC", "Vitamina C", "A11GA01", otc: true, taxCodeId: e.ExentoId));
        await e.ReceiveAsync(fixture, sinPrecio.Id, "L-1", InventoryScenario.InDays(200), 5, 1m);

        using var scope = fixture.Factory.Services.CreateScope();
        var h = Get<OverTheCounterSaleHandler>(scope);
        OverTheCounterSaleInput Venta(Guid id, IReadOnlyList<TenderLine>? cobro = null) =>
            new(Guid.NewGuid().ToString(), null, null, null, null, null, null, [new SaleLineInput(id, 1, null)], cobro);

        await FluentActions.Invoking(() => h.SellAsync(e.OrgId, e.UserId, Venta(receta.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*no es de venta libre*");
        await FluentActions.Invoking(() => h.SellAsync(e.OrgId, e.UserId, Venta(sinPrecio.Id)))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*todavía no tiene precio*");
        // Caja de servicios: el mostrador no cobra ahí.
        await FluentActions.Invoking(() => h.SellAsync(e.OrgId, e.UserId, Venta(item.Id, [new TenderLine("Cash", 5m, null)])))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*caja de farmacia o mixta*");

        var db = Get<AppDbContext>(scope);
        (await db.PharmacyDispenses.CountAsync(d => d.OrganizationId == e.OrgId)).Should().Be(0);
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(50m);
    }

    [Fact]
    public async Task VentaLibre_sinFactura_seAnula_yLaMercaderiaVuelve()
    {
        var (e, item, _) = await ArrangeVentaAsync(perfil: "Mixed");
        using var scope = fixture.Factory.Services.CreateScope();
        // Una venta normal, facturada.
        var r = await Get<OverTheCounterSaleHandler>(scope).SellAsync(e.OrgId, e.UserId, new OverTheCounterSaleInput(
            Guid.NewGuid().ToString(), null, null, null, "Comprador", null, null,
            [new SaleLineInput(item.Id, 4, null)], null));
        r.Invoice.Should().NotBeNull(r.InvoiceRejected?.Message);

        // Una venta que sí queda sin factura: la autorización de la sede se dio de baja.
        var db = Get<AppDbContext>(scope);
        var auth = await db.FiscalAuthorizations.AsNoTracking().SingleAsync(a => a.OrganizationId == e.OrgId);
        await Get<RevokeFiscalAuthorizationHandler>(scope).HandleAsync(e.OrgId, auth.Id);

        var sinFactura = await Get<OverTheCounterSaleHandler>(scope).SellAsync(e.OrgId, e.UserId, new OverTheCounterSaleInput(
            Guid.NewGuid().ToString(), null, null, null, null, null, null, [new SaleLineInput(item.Id, 6, null)], null));
        sinFactura.Invoice.Should().BeNull();
        sinFactura.InvoiceRejected!.Code.Should().Be("autorizacion_bloqueada");
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(40m);

        var anulada = await Get<OverTheCounterSaleHandler>(scope).CancelAsync(e.OrgId, e.UserId, sinFactura.Sale.Id, "El cliente se arrepintió");
        anulada!.Sale.Status.Should().Be("Cancelled");
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(46m);
        var cargo = await db.BillingCharges.AsNoTracking().SingleAsync(c => c.Id == sinFactura.Sale.Lines[0].ChargeId);
        cargo.Status.Should().Be(ChargeStatus.Reversed);

        // Lo facturado no se anula aquí.
        await FluentActions.Invoking(() => Get<OverTheCounterSaleHandler>(scope).CancelAsync(e.OrgId, e.UserId, r.Sale.Id, "x"))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*nota de crédito*");
    }

    // ── Devolución por nota de crédito (B6) ──────────────────────────────

    [Fact]
    public async Task NotaDeCredito_devuelveAlLoteOriginal_unaSolaVez_ySinPasarseDeLoEntregado()
    {
        var r = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var res = await Get<DispensePrescriptionHandler>(scope).HandleAsync(r.E.OrgId, r.E.UserId, Despacho(r, 14));
        var linea = res.Dispense.Lines.Single();
        var nota = Guid.NewGuid();
        var puerto = Get<IStockReturnPort>(scope);
        puerto.Should().BeOfType<StockReturnPort>("B6 reemplaza la implementación vacía de la Fase 0");

        var devolucion = new StockReturnLine(nota, linea.ChargeId!.Value, linea.Id.ToString(), r.Item.Id, 6m, r.E.LocationId);
        await puerto.ReturnFromCreditNoteAsync(r.E.OrgId, [devolucion]);
        await puerto.ReturnFromCreditNoteAsync(r.E.OrgId, [devolucion]); // reintento de la misma nota

        var db = Get<AppDbContext>(scope);
        var lotes = await (from b in db.StockBalances.AsNoTracking()
                           join l in db.StockLots.AsNoTracking() on b.StockLotId equals l.Id
                           where b.StockItemId == r.Item.Id
                           select new { l.LotCode, b.QuantityOnHand }).ToListAsync();
        // Salieron 10 de L-PRONTO y 4 de L-TARDE; vuelven 6: primero los 4 de L-TARDE, luego 2 de L-PRONTO.
        lotes.Single(l => l.LotCode == "L-TARDE").QuantityOnHand.Should().Be(10m);
        lotes.Single(l => l.LotCode == "L-PRONTO").QuantityOnHand.Should().Be(2m);
        (await db.StockMovements.CountAsync(m => m.StockItemId == r.Item.Id && m.Type == StockMovementType.CustomerReturn))
            .Should().Be(2);

        // Otra nota por más de lo que queda por devolver: vuelve solo lo que falta (8).
        await puerto.ReturnFromCreditNoteAsync(r.E.OrgId,
            [devolucion with { CreditNoteId = Guid.NewGuid(), Quantity = 20m }]);
        (await db.StockBalances.Where(b => b.StockItemId == r.Item.Id).SumAsync(b => b.QuantityOnHand)).Should().Be(20m);
        var constancia = await db.StockReturnRecords.AsNoTracking()
            .Where(x => x.ChargeId == linea.ChargeId).OrderBy(x => x.ProcessedAt).ToListAsync();
        constancia.Should().HaveCount(2);
        constancia[1].ReturnedQuantity.Should().Be(8m);
        constancia[1].Note.Should().Contain("Solo se podían devolver");
    }

    // ── Prueba conjunta S2 (plan §5): nota de crédito de facturación → inventario ──

    [Fact]
    public async Task NotaDeCreditoDeUnaVenta_devuelveLaMercaderia_yLaCajaEntregaElDinero()
    {
        var (e, item, _) = await ArrangeVentaAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var venta = await Get<OverTheCounterSaleHandler>(scope).SellAsync(e.OrgId, e.UserId, new OverTheCounterSaleInput(
            "venta-nc", null, null, null, null, null, null,
            [new SaleLineInput(item.Id, 10, null)],
            [new TenderLine("Cash", 20m, null)]));
        venta.Invoice.Should().NotBeNull(venta.InvoiceRejected?.Message);

        // La sede necesita su CAI de notas de crédito.
        var tipos = await Get<IFiscalRepository>(scope).ListDocumentTypesAsync(e.OrgId);
        await Get<RegisterFiscalAuthorizationHandler>(scope).HandleAsync(new RegisterFiscalAuthorizationCommand(
            e.OrgId, e.LocationId, tipos.Single(t => t.Code == "nota_credito").Id, "CAI-NC", "002", 1, 100,
            new DateOnly(2027, 12, 31)));

        var linea = venta.Invoice!.Lines.Single();
        var nota = await Get<IssueCreditNoteHandler>(scope).HandleAsync(new IssueCreditNoteCommand(
            e.OrgId, venta.Invoice.Id, "Devolvió 4 tabletas sin abrir", e.UserId,
            [new CreditNoteLineRequest(linea.Id!.Value, 4m)]));

        nota.Succeeded.Should().BeTrue(nota.Rejected?.Message);
        nota.CreditNote!.GrandTotal.Should().Be(8m);
        nota.CreditNote.RefundDue.Should().Be(8m, "la venta ya estaba cobrada");

        var db = Get<AppDbContext>(scope);
        (await db.StockBalances.Where(b => b.StockItemId == item.Id).SumAsync(b => b.QuantityOnHand))
            .Should().Be(44m, "salieron 10 de 50 y volvieron 4");

        var (entregada, error) = await Get<RefundCreditNoteHandler>(scope).HandleAsync(e.OrgId, nota.CreditNote.Id, e.UserId);
        error.Should().BeNull();
        entregada!.RefundedAt.Should().NotBeNull();
    }
}
