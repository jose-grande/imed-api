using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Cobrar una cuenta en un paso (UX de cobro, etapa 2): el cajero elige a quién le cobra y el
/// sistema factura lo que falte. Antes, una consulta pedía emitir en Facturas y cobrar en Caja.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AccountCheckoutTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid UserId, Guid AccountId, Guid CashPointId, Guid ItemId, Guid FacturaTypeId, Guid LocationId);

    private async Task<Escenario> ArrangeAsync(decimal? precio = 500m)
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(orgId, sede.Id, factura, "CAI-QA", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));

        // Sin precio: como lo deja el aprovisionamiento automático hasta que el jefe lo fija.
        var cobrable = BillableItem.Create(orgId, "CONS", "Consulta general", BillableItemKind.Service, exento,
            precio ?? 0m, pricePending: precio is null);
        await sp.GetRequiredService<IBillingCatalogRepository>().AddItemAsync(cobrable);
        var item = cobrable;
        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-co-1", "María Zelaya", $"enc-{Guid.NewGuid():N}", "Episode"));
        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(orgId, cuenta.Id, item.Id, ChargeSourceKind.Encounter, cuenta.EncounterFhirId!));

        return new Escenario(orgId, userId, cuenta.Id, caja.Id, item.Id, factura, sede.Id);
    }

    private async Task AbrirTurnoAsync(Escenario e)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, e.UserId, 0m, anyLocation: true)).Succeeded.Should().BeTrue();
    }

    private async Task<T> ConAsync<T>(Func<AccountCheckoutHandler, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<AccountCheckoutHandler>());
    }

    private static AccountCheckoutInput Efectivo(decimal monto, string? nombre = null, string? rtn = null) =>
        new(nombre, rtn, [new TenderLine("Cash", monto, null)]);

    [Fact]
    public async Task Cobrar_unaConsultaSinFacturar_laFacturaYLaCobraEnUnPaso()
    {
        var e = await ArrangeAsync();
        await AbrirTurnoAsync(e);

        var antes = await ConAsync(h => h.GetAsync(e.OrgId, e.UserId, e.AccountId));
        antes!.PendingToInvoice.Should().Be(500m);
        antes.TotalDue.Should().Be(500m);
        antes.CollectBlocker.Should().BeNull();
        antes.BuyerName.Should().Be("María Zelaya");

        var r = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId, Efectivo(600m)));

        r!.InvoiceRejected.Should().BeNull();
        r.Invoice!.GrandTotal.Should().Be(500m);
        r.Payment!.Succeeded.Should().BeTrue(r.Payment.Message);
        r.Payment.ChangeDue.Should().Be(100m);
        r.PaidInvoiceIds.Should().ContainSingle().Which.Should().Be(r.Invoice.Id);
        r.State.TotalDue.Should().Be(0m);

        // El segundo clic no factura ni cobra de nuevo.
        var otra = async () => await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId, Efectivo(600m)));
        await otra.Should().ThrowAsync<InvalidOperationException>().WithMessage("*nada pendiente*");
    }

    [Fact]
    public async Task ElNombreYRtnDelComprador_vanEnLaFactura()
    {
        var e = await ArrangeAsync();
        await AbrirTurnoAsync(e);

        var r = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId,
            Efectivo(500m, "Empresa S. de R.L.", "08019999000011")));

        r!.Invoice!.FiscalDocument!.BuyerName.Should().Be("Empresa S. de R.L.");
        r.Invoice.FiscalDocument.BuyerTaxId.Should().Be("08019999000011");
    }

    [Fact]
    public async Task FacturarSinCobrar_dejaLaFacturaPendiente_yDespuesSeCobra()
    {
        var e = await ArrangeAsync();

        var sinCobro = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId,
            new AccountCheckoutInput(null, null, null)));
        sinCobro!.Invoice.Should().NotBeNull("se factura sin turno: no hay dinero de por medio");
        sinCobro.Payment.Should().BeNull();
        sinCobro.State.BalanceDue.Should().Be(500m);
        sinCobro.State.PendingToInvoice.Should().Be(0m);

        await AbrirTurnoAsync(e);
        var cobro = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId, Efectivo(500m)));
        cobro!.Invoice.Should().BeNull("ya estaba facturada");
        cobro.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);
        cobro.PaidInvoiceIds.Should().ContainSingle().Which.Should().Be(sinCobro.Invoice!.Id);
    }

    [Fact]
    public async Task SinTurno_noCobra_yNoConsumeNumero()
    {
        var e = await ArrangeAsync();

        var cobrar = async () => await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId, Efectivo(500m)));

        await cobrar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*abrí un turno*");
        (await ConAsync(h => h.GetAsync(e.OrgId, e.UserId, e.AccountId)))!.OpenInvoices
            .Should().BeEmpty("validar la caja antes de emitir evita una factura huérfana");
    }

    [Fact]
    public async Task ConCargoSinPrecio_noFactura_yDiceCual()
    {
        var e = await ArrangeAsync(precio: null);
        await AbrirTurnoAsync(e);

        var estado = await ConAsync(h => h.GetAsync(e.OrgId, e.UserId, e.AccountId));
        estado!.InvoiceBlocker.Should().Contain("Consulta general").And.Contain("precio");

        var cobrar = async () => await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId, Efectivo(500m)));
        await cobrar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*precio*");
    }

    [Fact]
    public void ElFacturador_puedeEmitirSinCobrar_yElCajeroNo()
    {
        // D1: el facturador emite facturas (a crédito, aseguradoras). El cajero emite al cobrar.
        PermissionCatalog.BaseMatrix.Should().Contain(("billing_clerk", PermissionCatalog.BillingInvoice));
        PermissionCatalog.BaseMatrix.Should().NotContain(("cashier", PermissionCatalog.BillingInvoice));
        PermissionCatalog.BaseMatrix.Should().NotContain(("billing_clerk", PermissionCatalog.BillingCollect));
    }

    [Fact]
    public async Task LaFacturaSeEncuentra_porNumeroOComprador_sinPasarPorLaCuenta()
    {
        // Reimprimir o anular pedía llegar por Cuentas: no había búsqueda de facturas.
        var e = await ArrangeAsync();
        var emitida = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.AccountId,
            new AccountCheckoutInput("Empresa Uno", null, null)));
        var numero = emitida!.Invoice!.FiscalDocument!.FullNumber;

        using var scope = fixture.Factory.Services.CreateScope();
        var buscar = scope.ServiceProvider.GetRequiredService<SearchInvoicesHandler>();

        (await buscar.HandleAsync(new SearchInvoicesQuery(e.OrgId, Text: numero[^4..])))
            .Should().ContainSingle().Which.FullNumber.Should().Be(numero);
        (await buscar.HandleAsync(new SearchInvoicesQuery(e.OrgId, Text: "empresa uno")))
            .Should().ContainSingle().Which.Balance.Should().Be(500m);
        (await buscar.HandleAsync(new SearchInvoicesQuery(e.OrgId, OnlyWithBalance: true))).Should().ContainSingle();
        (await buscar.HandleAsync(new SearchInvoicesQuery(Guid.NewGuid(), Text: numero)))
            .Should().BeEmpty("otra organización no ve estas facturas");
    }
}
