using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.IntegrationTests.Infrastructure;
using IMed.Api.IntegrationTests.Inventory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Productos en la venta de la Caja (UX-Cobro-Anticipado F4): el cajero vende un producto de venta
/// libre junto con un servicio, en una sola cuenta y una sola factura. El producto descuenta
/// inventario por la venta libre de siempre, y Farmacia lo ve facturado.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CounterSaleProductsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(InventoryScenario E, StockItemDto Acetaminofen, StockItemDto Amoxicilina, Guid Curacion);

    /// <param name="perfilCaja">Perfil de la caja del turno: Mixed, Services o Pharmacy.</param>
    private async Task<Escenario> ArrangeAsync(string perfilCaja)
    {
        var e = await InventoryScenario.CreateAsync(fixture);
        var acet = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("ACET", "Acetaminofén 500 mg", "N02BE01", otc: true, taxCodeId: e.ExentoId));
        await e.ReceiveAsync(fixture, acet.Id, "L-1", InventoryScenario.InDays(200), 50, 0.5m);
        await e.SetPriceAsync(fixture, acet.BillableItemId!.Value, 2m);

        // Con receta: no es de venta libre.
        var amox = await e.CreateItemAsync(fixture,
            InventoryScenario.Medicamento("AMOX", "Amoxicilina 500 mg", "J01CA04", otc: false, taxCodeId: e.ExentoId));
        await e.ReceiveAsync(fixture, amox.Id, "L-2", InventoryScenario.InDays(200), 50, 1m);
        await e.SetPriceAsync(fixture, amox.BillableItemId!.Value, 5m);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var curacion = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(new CreateBillableItemCommand(
            e.OrgId, "CUR", "Curación simple", "Service", e.ExentoId, 150m, "Either", "Global", null, null, null));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(new CreateCashPointCommand(
            e.OrgId, e.LocationId, "CAJA1", "Caja 1", "002", perfilCaja, false, null));
        (await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(e.OrgId, caja.Id, e.UserId, 0m))
            .Succeeded.Should().BeTrue();
        return new Escenario(e, acet, amox, curacion.Id);
    }

    private async Task<T> ConAsync<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<CounterSaleResult> VenderAsync(Escenario s, IReadOnlyList<CounterSaleProduct> productos, bool conCuracion = true) =>
        ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().CreateAsync(s.E.OrgId, s.E.UserId, new CounterSaleInput(
            "pat-prod-1", "Ana Rivera", null,
            conCuracion ? [new CounterSaleProcedure(s.Curacion)] : null,
            Products: productos)));

    [Fact]
    public async Task ConCajaMixta_productoYServicio_vanEnUnaFactura_yFarmaciaLaVeFacturada()
    {
        var s = await ArrangeAsync("Mixed");

        var ofrecidos = await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>()
            .SearchAsync(s.E.OrgId, s.E.UserId, "acetamin"));
        ofrecidos.Should().ContainSingle(x => x.Kind == "product").Which.UnitPrice.Should().Be(2m);

        var venta = await VenderAsync(s, [new CounterSaleProduct(s.Acetaminofen.Id, 10)]);

        var cargos = await ConAsync(sp => sp.GetRequiredService<IBillingAccountRepository>().ListChargesAsync(s.E.OrgId, venta.AccountId));
        cargos.Select(c => c.SourceKind).Should().BeEquivalentTo([ChargeSourceKind.PharmacyDispense, ChargeSourceKind.Manual],
            "el producto lo carga la venta libre, a la misma cuenta");

        var cobro = await ConAsync(sp => sp.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
            s.E.OrgId, s.E.UserId, venta.AccountId, new AccountCheckoutInput(null, null, [new TenderLine("Cash", 170m, null)])));
        cobro!.Invoice!.GrandTotal.Should().Be(170m, "10 × 2 + 150");
        cobro.Invoice.Lines.Should().HaveCount(2, "una sola factura con el producto y la curación");
        cobro.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);

        // Farmacia ve la venta facturada: no se puede anular lo ya cobrado.
        var despacho = (await ConAsync(sp => sp.GetRequiredService<IInventoryRepository>()
            .ListDispensesByAccountAsync(s.E.OrgId, venta.AccountId))).Single();
        despacho.InvoiceId.Should().Be(cobro.Invoice.Id);
        despacho.Lines.Single().Quantity.Should().Be(10m, "descontó del inventario lo vendido");
    }

    [Theory]
    [InlineData("Services")]
    [InlineData("Pharmacy")]
    public async Task SinCajaMixta_losProductosNiSeOfrecenNiSeVenden(string perfil)
    {
        // Una caja de servicios no cobra productos; una de farmacia no cobra servicios.
        var s = await ArrangeAsync(perfil);

        (await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().SearchAsync(s.E.OrgId, s.E.UserId, "acetamin")))
            .Should().NotContain(x => x.Kind == "product");
        await FluentActions.Invoking(() => VenderAsync(s, [new CounterSaleProduct(s.Acetaminofen.Id, 1)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*caja mixta*");
    }

    [Fact]
    public async Task UnMedicamentoConReceta_noSeVendeEnCaja()
    {
        var s = await ArrangeAsync("Mixed");

        (await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().SearchAsync(s.E.OrgId, s.E.UserId, "amoxic")))
            .Should().NotContain(x => x.Kind == "product", "solo se ofrece lo de venta libre");
        await FluentActions.Invoking(() => VenderAsync(s, [new CounterSaleProduct(s.Amoxicilina.Id, 1)]))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*venta libre*");
    }

    [Fact]
    public async Task SoloProductos_tambienSeVendenEnLaCaja()
    {
        var s = await ArrangeAsync("Mixed");

        var venta = await VenderAsync(s, [new CounterSaleProduct(s.Acetaminofen.Id, 3)], conCuracion: false);
        var cobro = await ConAsync(sp => sp.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
            s.E.OrgId, s.E.UserId, venta.AccountId, new AccountCheckoutInput(null, null, [new TenderLine("Cash", 6m, null)])));

        cobro!.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);
    }
}
