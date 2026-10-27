using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Inventory;
using IMed.Api.Application.Pharmacy;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Inventory;

/// <summary>
/// Inventario y farmacia por HTTP: permisos por rol (F0.7) y los reportes (B7) con el corte de día
/// de la clínica.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class InventoryEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync()
    {
        fixture.Hapi.EnablePharmacy();
        return fixture.ResetStateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient Cliente(InventoryScenario e, params string[] roles)
    {
        fixture.Keycloak.RegisterUser(e.UserId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            e.UserId, organizationId: e.OrgId.ToString(), realmRoles: roles));
    }

    [Fact]
    public async Task Bodeguero_registraEntradas_peroNoConfiguraArticulos_niVeTodasLasSedes()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var bodeguero = Cliente(e, "warehouse_keeper");
        var gerente = Cliente(e, "inventory_manager");

        var nuevo = InventoryScenario.Insumo("VENDA", "Venda elástica");
        (await bodeguero.PostAsJsonAsync("/api/inventory/items", nuevo)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var creado = await gerente.PostAsJsonAsync("/api/inventory/items", nuevo);
        creado.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = (await creado.Content.ReadFromJsonAsync<StockItemDto>())!;

        var entrada = await bodeguero.PostAsJsonAsync("/api/inventory/receipts", new GoodsReceiptInput(
            null, e.SupplierId, "F-100", InventoryScenario.InDays(0), null,
            [new GoodsReceiptLineInput(item.Id, null, null, null, 12, 3m)]));
        entrada.StatusCode.Should().Be(HttpStatusCode.Created, await entrada.Content.ReadAsStringAsync());

        var stock = await bodeguero.GetFromJsonAsync<List<StockLineDto>>("/api/inventory/stock");
        stock!.Should().ContainSingle(s => s.StockItemId == item.Id).Which.Value.Should().Be(36m);

        (await bodeguero.GetAsync("/api/inventory/stock?all=true")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await gerente.GetAsync("/api/inventory/stock?all=true")).StatusCode.Should().Be(HttpStatusCode.OK);

        // Repetir la misma factura del mismo proveedor: conflicto con mensaje, no un 500.
        var repetida = await bodeguero.PostAsJsonAsync("/api/inventory/receipts", new GoodsReceiptInput(
            null, e.SupplierId, "F-100", InventoryScenario.InDays(0), null,
            [new GoodsReceiptLineInput(item.Id, null, null, null, 12, 3m)]));
        repetida.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await repetida.Content.ReadAsStringAsync()).Should().Contain("ya está registrada");
    }

    [Fact]
    public async Task SinExistencia_respondeConflictoConMensaje()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("TORUNDA", "Torundas"));
        await e.ReceiveAsync(fixture, item.Id, null, null, 2, 1m);
        var bodeguero = Cliente(e, "warehouse_keeper");

        var r = await bodeguero.PostAsJsonAsync("/api/inventory/consumptions",
            new ConsumptionInput(null, "Curaciones", null, [new StockOutLineInput(item.Id, 5, null)]));
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await r.Content.ReadAsStringAsync()).Should().Contain("No alcanza la existencia de Torundas");
    }

    [Fact]
    public async Task Farmacia_porRol_laAuxiliarVendeYDespacha_elBodegueroNo()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var item = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("ANTIA", "Antiácido", "A02AD01", otc: true, taxCodeId: e.ExentoId));
        await e.ReceiveAsync(fixture, item.Id, "L-1", InventoryScenario.InDays(200), 10, 1m);
        await e.SetPriceAsync(fixture, item.BillableItemId!.Value, 3m);

        var auxiliar = Cliente(e, "pharmacy_tech");
        (await auxiliar.GetAsync("/api/pharmacy/queue")).StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await auxiliar.GetFromJsonAsync<List<DispenseCandidateDto>>("/api/pharmacy/sale-items?q=antia");
        items.Should().ContainSingle().Which.Price.Should().Be(3m);

        var venta = await auxiliar.PostAsJsonAsync("/api/pharmacy/sales", new OverTheCounterSaleInput(
            Guid.NewGuid().ToString(), null, null, null, null, null, null, [new SaleLineInput(item.Id, 2, null)], null));
        venta.StatusCode.Should().Be(HttpStatusCode.Created, await venta.Content.ReadAsStringAsync());
        var r = (await venta.Content.ReadFromJsonAsync<SaleResultDto>())!;
        r.Invoice!.GrandTotal.Should().Be(6m);

        // El mostrador imprime su factura sin el permiso de lectura de todo facturación.
        var pdf = await auxiliar.GetAsync($"/api/pharmacy/sales/{r.Sale.Id}/invoice/pdf?format=thermal80");
        pdf.StatusCode.Should().Be(HttpStatusCode.OK);
        pdf.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await auxiliar.GetAsync($"/api/billing/invoices/{r.Invoice.Id}/pdf")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var bodeguero = Cliente(e, "warehouse_keeper");
        (await bodeguero.GetAsync("/api/pharmacy/queue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await bodeguero.GetAsync($"/api/pharmacy/dispenses/{r.Sale.Id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reportes_cortanElDiaEnLaHoraDeLaClinica_yLaValorizacionCuadra()
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var a = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("A1", "Apósito"));
        var b = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("B1", "Bisturí"));
        await e.ReceiveAsync(fixture, a.Id, null, null, 10, 2m);
        await e.ReceiveAsync(fixture, b.Id, null, null, 4, 5m);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<StockMovementHandlers>().ConsumeAsync(e.OrgId, e.UserId,
                new ConsumptionInput(null, "Quirófano", null, [new StockOutLineInput(b.Id, 1, null)]));
        }

        DateOnly hoy;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            hoy = scope.ServiceProvider.GetRequiredService<IClinicClock>().Today();
        }
        var gerente = Cliente(e, "inventory_manager");

        var valor = await gerente.GetFromJsonAsync<ValuationReportDto>("/api/inventory/reports/valuation");
        valor!.Total.Should().Be(35m, "10 × 2 + 3 × 5");
        valor.Warehouses.Should().ContainSingle().Which.Value.Should().Be(35m);

        var movs = await gerente.GetFromJsonAsync<MovementReportDto>(
            $"/api/inventory/reports/movements?from={hoy:yyyy-MM-dd}&to={hoy:yyyy-MM-dd}");
        movs!.Rows.Should().HaveCount(3);
        movs.TotalIn.Should().Be(40m);
        movs.TotalOut.Should().Be(5m);

        var ayer = hoy.AddDays(-1);
        var vacio = await gerente.GetFromJsonAsync<MovementReportDto>(
            $"/api/inventory/reports/movements?from={ayer:yyyy-MM-dd}&to={ayer:yyyy-MM-dd}");
        vacio!.Rows.Should().BeEmpty("lo de hoy (hora de Honduras) no cae en el día anterior");

        var consumo = await gerente.GetFromJsonAsync<ConsumptionReportDto>(
            $"/api/inventory/reports/consumption?from={hoy:yyyy-MM-dd}&to={hoy:yyyy-MM-dd}");
        consumo!.Rows.Should().ContainSingle().Which.Area.Should().Be("Quirófano");
        consumo.TotalCost.Should().Be(5m);

        var vence = await gerente.GetFromJsonAsync<List<ExpiringLotDto>>("/api/inventory/reports/expiring?days=90");
        vence.Should().BeEmpty("los insumos sin lote no vencen");
    }
}
