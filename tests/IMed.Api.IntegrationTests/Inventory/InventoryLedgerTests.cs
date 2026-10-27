using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace IMed.Api.IntegrationTests.Inventory;

/// <summary>
/// Inventario (plan de salida a producción, B1–B2): entradas, costo promedio, FEFO, existencias sin
/// negativos, traslados, consumo, ajustes con aprobación, conteo físico y el libro append-only.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class InventoryLedgerTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    // ── Bodegas (P7) ─────────────────────────────────────────────────────

    [Fact]
    public async Task CadaSede_tieneSuBodegaGeneral_creadaSola_yUnaSolaVez()
    {
        var e = await InventoryScenario.CreateAsync(fixture, withRegime: false);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = Get<AppDbContext>(scope);
            db.OrganizationLocations.Add(TestSedes.Create(db, e.OrgId, "NORTE", "Sede norte"));
            await db.SaveChangesAsync();
        }

        // Dos lecturas en paralelo: el índice único decide y no se duplica.
        var tareas = Enumerable.Range(0, 3).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return await Get<WarehouseHandlers>(scope).ListAsync(e.OrgId, null, false);
        });
        await Task.WhenAll(tareas);

        using var verify = fixture.Factory.Services.CreateScope();
        var bodegas = await Get<WarehouseHandlers>(verify).ListAsync(e.OrgId, null, false);
        bodegas.Should().HaveCount(2).And.OnlyContain(b => b.IsGeneral && b.Kind == "General");
        bodegas.Select(b => b.LocationId).Distinct().Should().HaveCount(2);
    }

    // ── B2: vendible ⇒ cobrable "sin precio" ─────────────────────────────

    [Fact]
    public async Task ArticuloVendible_creaSuCobrableSinPrecio_conElTributoDelArticulo()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("AMOX500", "Amoxicilina 500 mg", "J01CA04", taxCodeId: e.ExentoId));

        item.BillableItemId.Should().NotBeNull();
        item.PricePending.Should().BeTrue();

        using var scope = fixture.Factory.Services.CreateScope();
        var cobrable = await Get<AppDbContext>(scope).BillableItems.AsNoTracking().SingleAsync(b => b.Id == item.BillableItemId);
        cobrable.StockItemId.Should().Be(item.Id);
        cobrable.TaxCodeId.Should().Be(e.ExentoId);
        cobrable.Code.Should().Be("ART-AMOX500");

        // Guardar otra vez no crea otro cobrable.
        var otra = await Get<StockItemCommandHandler>(scope).UpdateAsync(e.OrgId, item.Id,
            InventoryScenario.Medicamento("AMOX500", "Amoxicilina 500 mg (Cápsulas)", "J01CA04", taxCodeId: e.ExentoId));
        otra!.BillableItemId.Should().Be(item.BillableItemId);
        (await Get<AppDbContext>(scope).BillableItems.CountAsync(b => b.OrganizationId == e.OrgId && b.StockItemId == item.Id))
            .Should().Be(1);
    }

    [Fact]
    public async Task ArticuloVendible_conTributoAjeno_seRechaza()
    {
        var e = await InventoryScenario.CreateAsync(fixture, withRegime: false);
        // Sin régimen no hay tributos propios: un tributo que no es de la organización no vale.
        var crear = () => e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("IBU400", "Ibuprofeno 400 mg", "M01AE01", taxCodeId: Guid.NewGuid()));
        await crear.Should().ThrowAsync<ArgumentException>().WithMessage("*tributo*");
    }

    [Fact]
    public async Task MedicamentoSinAtc_seRechaza_yLosReactivosExigenLote()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var sinAtc = () => e.CreateItemAsync(fixture, InventoryScenario.Medicamento("X1", "Algo", "", sellable: false));
        await sinAtc.Should().ThrowAsync<ArgumentException>().WithMessage("*ATC*");

        var reactivo = await e.CreateItemAsync(fixture, new StockItemInput(
            "REACT-GLU", "Reactivo glucosa", "Reagent", "prueba", false, null, false, false, null, null, true,
            null, null, null, null, null, null, "2–8 °C", 30, null, null, null));
        reactivo.RequiresLot.Should().BeTrue("D8: los reactivos llevan lote siempre");
    }

    // ── Entradas y costo promedio (D7, D13) ──────────────────────────────

    [Fact]
    public async Task Entradas_creanLotes_yElCostoEsPromedioPonderadoPorBodega()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Medicamento("PARA500", "Paracetamol 500 mg", "N02BE01", sellable: false));
        var bodega = await e.GeneralWarehouseAsync(fixture);

        // 1 caja x 10 a L 50 la caja → 10 tabletas a L 5.
        await e.ReceiveAsync(fixture, item.Id, "L-A", InventoryScenario.InDays(200), 1, 50m, presentation: "Caja x 10");
        // 30 tabletas a L 9.
        await e.ReceiveAsync(fixture, item.Id, "L-B", InventoryScenario.InDays(400), 30, 9m);

        using var scope = fixture.Factory.Services.CreateScope();
        var costo = await Get<IStockLedger>(scope).GetAverageCostAsync(bodega.Id, item.Id);
        costo.Should().Be(8m, "(10 × 5 + 30 × 9) / 40");

        var existencias = await Get<InventoryQueryHandlers>(scope).StockAsync(e.OrgId, e.UserId, bodega.Id, item.Id, null, false, false);
        existencias.Should().HaveCount(2);
        existencias.Sum(x => x.QuantityOnHand).Should().Be(40m);

        var kardex = await Get<InventoryQueryHandlers>(scope).KardexAsync(e.OrgId, e.UserId, bodega.Id, item.Id, null, null);
        kardex.Should().HaveCount(2);
        kardex[0].QuantityAfter.Should().Be(10m);
        kardex[0].UnitCost.Should().Be(5m);
        kardex[1].QuantityAfter.Should().Be(40m);
        kardex[1].AverageCostAfter.Should().Be(8m);
    }

    [Fact]
    public async Task Entrada_validaLoteVencimientoYFacturaRepetida()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Medicamento("LORA10", "Loratadina 10 mg", "R06AX13", sellable: false));

        var sinLote = () => e.ReceiveAsync(fixture, item.Id, null, null, 10, 1m);
        await sinLote.Should().ThrowAsync<ArgumentException>().WithMessage("*exige lote*");

        var sinVence = () => e.ReceiveAsync(fixture, item.Id, "L1", null, 10, 1m);
        await sinVence.Should().ThrowAsync<ArgumentException>().WithMessage("*vencimiento*");

        var vencido = () => e.ReceiveAsync(fixture, item.Id, "L1", InventoryScenario.InDays(-1), 10, 1m);
        await vencido.Should().ThrowAsync<ArgumentException>().WithMessage("*no se recibe mercadería vencida*");

        await e.ReceiveAsync(fixture, item.Id, "L1", InventoryScenario.InDays(100), 10, 1m, invoice: "F-001");
        var otroVence = () => e.ReceiveAsync(fixture, item.Id, "L1", InventoryScenario.InDays(150), 10, 1m);
        await otroVence.Should().ThrowAsync<ArgumentException>().WithMessage("*un lote tiene un solo vencimiento*");

        var repetida = () => e.ReceiveAsync(fixture, item.Id, "L1", InventoryScenario.InDays(100), 10, 1m, invoice: "F-001");
        await repetida.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ya está registrada*");
    }

    [Fact]
    public async Task AnularEntrada_deshaceElPromedio_ySiYaSalioParteNoSePuede()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("GASA", "Gasa estéril"));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, item.Id, null, null, 10, 2m);
        var segunda = await e.ReceiveAsync(fixture, item.Id, null, null, 10, 4m);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            (await Get<IStockLedger>(scope).GetAverageCostAsync(bodega.Id, item.Id)).Should().Be(3m);
            (await Get<GoodsReceiptHandlers>(scope).VoidAsync(e.OrgId, e.UserId, segunda, "Factura duplicada")).Should().BeTrue();
            (await Get<IStockLedger>(scope).GetAverageCostAsync(bodega.Id, item.Id)).Should().Be(2m, "anular la compra la saca del promedio");
        }

        var tercera = await e.ReceiveAsync(fixture, item.Id, null, null, 5, 2m);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Get<StockMovementHandlers>(scope).ConsumeAsync(e.OrgId, e.UserId,
                new ConsumptionInput(bodega.Id, "Curaciones", null, [new StockOutLineInput(item.Id, 12, null)]));
            var anular = () => Get<GoodsReceiptHandlers>(scope).VoidAsync(e.OrgId, e.UserId, tercera, "Error");
            await anular.Should().ThrowAsync<InsufficientStockException>().WithMessage("*ya salió*");
        }
    }

    // ── Salidas: FEFO, sin negativos, concurrencia (D8, P6) ──────────────

    [Fact]
    public async Task Consumo_salePorFefo_ySaltaLosVencidosYBloqueados()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Medicamento("DICLO", "Diclofenaco", "M01AB05", sellable: false));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, item.Id, "TARDE", InventoryScenario.InDays(300), 10, 1m);
        await e.ReceiveAsync(fixture, item.Id, "PRONTO", InventoryScenario.InDays(30), 10, 1m);
        await e.ReceiveAsync(fixture, item.Id, "BLOQ", InventoryScenario.InDays(10), 10, 1m);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(scope);
        var bloq = await db.StockLots.SingleAsync(l => l.StockItemId == item.Id && l.LotCode == "BLOQ");
        bloq.SetStatus(StockLotStatus.Blocked);
        await db.SaveChangesAsync();

        var propuesta = await Get<InventoryQueryHandlers>(scope).FefoAsync(e.OrgId, e.UserId, bodega.Id, item.Id, 15, false);
        propuesta.Sufficient.Should().BeTrue();
        propuesta.Lots.Select(l => (l.LotCode, l.Quantity)).Should().Equal(("PRONTO", 10m), ("TARDE", 5m));

        var id = await Get<StockMovementHandlers>(scope).ConsumeAsync(e.OrgId, e.UserId,
            new ConsumptionInput(bodega.Id, "Sala de procedimientos", null, [new StockOutLineInput(item.Id, 15, null)]));
        var consumo = await Get<InventoryQueryHandlers>(scope).ConsumptionAsync(e.OrgId, id);
        consumo!.Lines.Select(l => (l.LotCode, l.Quantity)).Should().BeEquivalentTo([("PRONTO", 10m), ("TARDE", 5m)]);
        consumo.Total.Should().Be(15m);

        // El lote bloqueado no se consume aunque se pida a mano.
        var aMano = () => Get<StockMovementHandlers>(scope).ConsumeAsync(e.OrgId, e.UserId,
            new ConsumptionInput(bodega.Id, "Sala", null, [new StockOutLineInput(item.Id, 1, bloq.Id)]));
        await aMano.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bloqueado*");
    }

    [Fact]
    public async Task Salida_sinExistencia_noDejaNadaAMedias()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var a = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("JER5", "Jeringa 5 mL"));
        var b = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("GUANTE", "Guantes"));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, a.Id, null, null, 10, 1m);
        await e.ReceiveAsync(fixture, b.Id, null, null, 2, 1m);

        using var scope = fixture.Factory.Services.CreateScope();
        // La primera línea alcanza, la segunda no: no sale ninguna.
        var consumo = () => Get<StockMovementHandlers>(scope).ConsumeAsync(e.OrgId, e.UserId,
            new ConsumptionInput(bodega.Id, "Emergencia", null,
                [new StockOutLineInput(a.Id, 5, null), new StockOutLineInput(b.Id, 3, null)]));
        await consumo.Should().ThrowAsync<InsufficientStockException>().WithMessage("*Guantes*hay 2*");

        using var verify = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(verify);
        (await db.StockBalances.Where(x => x.StockItemId == a.Id).SumAsync(x => x.QuantityOnHand)).Should().Be(10m);
        (await db.StockMovements.CountAsync(m => m.StockItemId == a.Id)).Should().Be(1, "solo la entrada");
        (await db.InternalConsumptions.CountAsync(c => c.OrganizationId == e.OrgId)).Should().Be(0);
    }

    [Fact]
    public async Task DosSalidasALaVez_deLaUltimaUnidad_unaFallaLimpio()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("CAT", "Catéter"));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, item.Id, null, null, 1, 30m);

        var tareas = Enumerable.Range(0, 4).Select(async i =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            try
            {
                await Get<StockMovementHandlers>(scope).ConsumeAsync(e.OrgId, e.UserId,
                    new ConsumptionInput(bodega.Id, $"Área {i}", null, [new StockOutLineInput(item.Id, 1, null)]));
                return true;
            }
            catch (InsufficientStockException)
            {
                return false;
            }
        });
        var resultados = await Task.WhenAll(tareas);

        resultados.Count(r => r).Should().Be(1);
        using var verify = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(verify);
        (await db.StockBalances.Where(x => x.StockItemId == item.Id).SumAsync(x => x.QuantityOnHand)).Should().Be(0m);
        (await db.StockMovements.CountAsync(m => m.StockItemId == item.Id && m.Direction == -1)).Should().Be(1);
        (await db.StockItemCosts.SingleAsync(c => c.StockItemId == item.Id)).QuantityOnHand.Should().Be(0m);
    }

    [Fact]
    public async Task ElLibro_esAppendOnly_enLaBase()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("ALC", "Alcohol"));
        await e.ReceiveAsync(fixture, item.Id, null, null, 3, 1m);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = Get<AppDbContext>(scope);
        var editar = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE stock_movement SET quantity = 300 WHERE stock_item_id = {item.Id}");
        (await editar.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("append-only");
        var borrar = () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM stock_movement WHERE stock_item_id = {item.Id}");
        await borrar.Should().ThrowAsync<PostgresException>();

        // Y el saldo no puede quedar negativo ni a mano (P6).
        var negativo = () => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE stock_balance SET quantity_on_hand = -1 WHERE stock_item_id = {item.Id}");
        await negativo.Should().ThrowAsync<PostgresException>();
    }

    // ── Traslados ────────────────────────────────────────────────────────

    [Fact]
    public async Task Traslado_saleYEntraAlMismoCosto_enElMismoGrupo()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Medicamento("OME20", "Omeprazol 20 mg", "A02BC01", sellable: false));
        var general = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, item.Id, "L-1", InventoryScenario.InDays(200), 20, 3m);

        using var scope = fixture.Factory.Services.CreateScope();
        var farmacia = await Get<WarehouseHandlers>(scope).CreateAsync(
            e.OrgId, e.UserId, new WarehouseInput(e.LocationId, "FARM-C", "Farmacia central", "Pharmacy", true));
        var id = await Get<StockMovementHandlers>(scope).TransferAsync(e.OrgId, e.UserId,
            new TransferInput(general.Id, farmacia.Id, "Reposición", [new StockOutLineInput(item.Id, 8, null)]));

        var t = await Get<InventoryQueryHandlers>(scope).TransferAsync(e.OrgId, id);
        t!.Lines.Should().ContainSingle().Which.UnitCost.Should().Be(3m);
        (await Get<IStockLedger>(scope).GetAverageCostAsync(farmacia.Id, item.Id)).Should().Be(3m);

        var db = Get<AppDbContext>(scope);
        var movs = await db.StockMovements.Where(m => m.GroupId == id).ToListAsync();
        movs.Should().HaveCount(2);
        movs.Sum(m => m.Direction * m.Quantity).Should().Be(0m, "partida doble");
        var stock = await Get<InventoryQueryHandlers>(scope).StockAsync(e.OrgId, e.UserId, null, item.Id, null, false, false);
        stock.Single(s => s.WarehouseId == farmacia.Id).QuantityOnHand.Should().Be(8m);
        stock.Single(s => s.WarehouseId == general.Id).QuantityOnHand.Should().Be(12m);
    }

    // ── Ajustes con aprobación (P8) y conteo físico ──────────────────────

    [Fact]
    public async Task Ajuste_sobreElUmbral_esperaAlGerente_yDebajoSeAplicaSolo()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Medicamento("INS", "Insulina", "A10AB01", sellable: false));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, item.Id, "L-INS", InventoryScenario.InDays(200), 20, 100m);

        using var scope = fixture.Factory.Services.CreateScope();
        var lote = await Get<AppDbContext>(scope).StockLots.SingleAsync(l => l.StockItemId == item.Id);
        var h = Get<AdjustmentHandlers>(scope);

        // L 1,500 a costo: pasa del umbral por defecto (L 1,000) y el bodeguero no aprueba.
        var grande = await h.CreateAsync(e.OrgId, e.UserId, canApprove: false, new AdjustmentInput(
            bodega.Id, "Damaged", "Se rompió la refrigeradora",
            [new AdjustmentLineInput(item.Id, -1, 15, lote.Id, null, null, null)]));
        grande.Status.Should().Be("PendingApproval");
        grande.Value.Should().Be(1500m);
        grande.Threshold.Should().Be(InventorySettings.DefaultAdjustmentApprovalThreshold);
        (await Get<AppDbContext>(scope).StockBalances.SingleAsync(b => b.StockLotId == lote.Id)).QuantityOnHand.Should().Be(20m);

        var alertas = await Get<InventoryQueryHandlers>(scope).AlertsAsync(e.OrgId, e.UserId);
        alertas.PendingAdjustments.Should().Be(1);

        var aprobado = await h.ApproveAsync(e.OrgId, Guid.NewGuid(), grande.Id);
        aprobado!.Status.Should().Be("Posted");

        // L 300: se aplica sin esperar.
        var chico = await h.CreateAsync(e.OrgId, e.UserId, canApprove: false, new AdjustmentInput(
            bodega.Id, "Expired", null, [new AdjustmentLineInput(item.Id, -1, 3, lote.Id, null, null, null)]));
        chico.Status.Should().Be("Posted");

        using var verify = fixture.Factory.Services.CreateScope();
        (await Get<AppDbContext>(verify).StockBalances.SingleAsync(b => b.StockLotId == lote.Id)).QuantityOnHand.Should().Be(2m);

        // Motivo fuera de la lista: no.
        var sinMotivo = () => h.CreateAsync(e.OrgId, e.UserId, false, new AdjustmentInput(
            bodega.Id, "porque sí", null, [new AdjustmentLineInput(item.Id, -1, 1, lote.Id, null, null, null)]));
        await sinMotivo.Should().ThrowAsync<ArgumentException>().WithMessage("*motivo*");
    }

    [Fact]
    public async Task ConteoFisico_conDiferencias_generaSuAjusteLigado()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var gasa = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("GASA2", "Gasa"));
        var jer = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("JER3", "Jeringa 3 mL"));
        var bodega = await e.GeneralWarehouseAsync(fixture);
        await e.ReceiveAsync(fixture, gasa.Id, null, null, 50, 1m);
        await e.ReceiveAsync(fixture, jer.Id, null, null, 20, 2m);

        using var scope = fixture.Factory.Services.CreateScope();
        var h = Get<StockCountHandlers>(scope);
        var id = await h.StartAsync(e.OrgId, e.UserId, bodega.Id, "Cierre de mes");
        var otro = () => h.StartAsync(e.OrgId, e.UserId, bodega.Id, null);
        await otro.Should().ThrowAsync<InvalidOperationException>().WithMessage("*conteo abierto*");

        var conteo = await Get<InventoryQueryHandlers>(scope).CountAsync(e.OrgId, id);
        conteo!.Lines.Should().HaveCount(2);
        await h.RecordAsync(e.OrgId, e.UserId, id, new CountRecordInput(conteo.Lines
            .Select(l => new CountRecordLine(l.Id, l.StockItemId == gasa.Id ? 47 : 21)).ToList()));

        var cierre = await h.CloseAsync(e.OrgId, e.UserId, canApprove: false, id);
        cierre!.Adjustment.Should().NotBeNull();
        cierre.Adjustment!.Status.Should().Be("Posted", "L 3 + L 2 no llega al umbral");

        var ajuste = await Get<InventoryQueryHandlers>(scope).AdjustmentAsync(e.OrgId, cierre.Adjustment.Id);
        ajuste!.Reason.Should().Be("CountDifference");
        ajuste.StockCountId.Should().Be(id);
        ajuste.Lines.Should().HaveCount(2);

        var stock = await Get<InventoryQueryHandlers>(scope).StockAsync(e.OrgId, e.UserId, bodega.Id, null, null, false, false);
        stock.Single(s => s.StockItemId == gasa.Id).QuantityOnHand.Should().Be(47m);
        stock.Single(s => s.StockItemId == jer.Id).QuantityOnHand.Should().Be(21m);
    }

    // ── Alertas (P9) ─────────────────────────────────────────────────────

    [Fact]
    public async Task Alertas_muestranPorVencerYBajoMinimo_deLaSede()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("SALB", "Salbutamol inhalador", "R03AC02", sellable: false, minimum: 10));
        await e.ReceiveAsync(fixture, item.Id, "PRONTO", InventoryScenario.InDays(20), 3, 50m);
        await e.ReceiveAsync(fixture, item.Id, "LEJOS", InventoryScenario.InDays(365), 2, 50m);

        using var scope = fixture.Factory.Services.CreateScope();
        var alertas = await Get<InventoryQueryHandlers>(scope).AlertsAsync(e.OrgId, e.UserId);
        alertas.ExpiryAlertDays.Should().Be(90);
        alertas.Expiring.Should().ContainSingle(l => l.LotCode == "PRONTO").Which.DaysToExpiry.Should().BeInRange(19, 21);
        alertas.BelowMinimum.Should().ContainSingle(x => x.StockItemId == item.Id).Which.OnHand.Should().Be(5m);
    }
}
