using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.LabReception;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Facturar y cobrar una orden externa desde la recepción del laboratorio: el laboratorio chico,
/// donde quien recibe al paciente también cobra.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabCheckoutTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid UserId, Guid CashPointId, string Requisition);

    /// <summary>Sede con CAI, caja de servicios, hemograma a L 350 y una orden externa admitida.</summary>
    private async Task<Escenario> ArrangeAsync(decimal? precio = 350m, string perfilCaja = "Services", bool deConsulta = false)
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var requisicion = $"ORD-{Guid.NewGuid():N}"[..16];
        await fixture.ProvisionLabCatalogAsync(orgId);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Laboratorio de Prueba");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "CAI-LAB", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Recepción", "002", perfilCaja, false, null));

        if (precio is { } p)
        {
            var lab = sp.GetRequiredService<ILabCatalogRepository>();
            var panel = (await lab.ListPanelsAsync(orgId)).First(x => x.Code == "CBC");
            var cobrable = await sp.GetRequiredService<IBillingCatalogRepository>().FindAnyByLabAsync(orgId, null, panel.Id);
            await sp.GetRequiredService<UpdateBillableItemHandler>().HandleAsync(new UpdateBillableItemCommand(
                orgId, cobrable!.Id, "Hemograma completo", exento, p, "Prepaid", Active: true));
        }

        fixture.Hapi.SeedServiceRequest(
            "pat-chk-1", loinc: null, "Hemograma completo",
            encounter: deConsulta ? $"enc-{Guid.NewGuid():N}" : null!, requisition: requisicion,
            localCode: "CBC", localSystem: "http://imed.local/panel");
        if (!deConsulta)
        {
            await sp.GetRequiredService<CheckInOrderHandler>().HandleAsync(orgId, requisicion, userId);
        }
        return new Escenario(orgId, userId, caja.Id, requisicion);
    }

    private async Task AbrirTurnoAsync(Escenario e)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, e.UserId, 500m);
        r.Succeeded.Should().BeTrue(r.Message);
    }

    private async Task<T> ConAsync<T>(Func<LabOrderCheckoutHandler, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<LabOrderCheckoutHandler>());
    }

    [Fact]
    public async Task FacturarYCobrar_enLaRecepcion_dejaElExamenPagado_yRepetirNoDuplica()
    {
        var e = await ArrangeAsync();

        var antes = await ConAsync(h => h.GetAsync(e.OrgId, e.UserId, e.Requisition));
        antes!.InvoiceBlocker.Should().BeNull();
        antes.PendingToInvoice.Should().Be(350m);
        antes.CollectBlocker.Should().Contain("abrí un turno", "sin turno se puede facturar pero no cobrar");

        await AbrirTurnoAsync(e);
        var r = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.Requisition,
            new LabCheckoutInput(null, null, [new TenderLine("Cash", 400m, null)])));

        r!.InvoiceRejected.Should().BeNull();
        r.Invoice!.GrandTotal.Should().Be(350m);
        r.Payment!.Succeeded.Should().BeTrue(r.Payment.Message);
        r.Payment.ChangeDue.Should().Be(50m);
        r.State.BalanceDue.Should().Be(0m);
        r.State.PendingToInvoice.Should().Be(0m);

        // La puerta de prepago lo ve pagado: el examen sigue a la toma sin aviso.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var orden = (await sp.GetRequiredService<IHapiFhirClient>()
                .SearchLabOrdersByRequisitionAsync(e.Requisition, $"org-{e.OrgId}")).Single();
            (await sp.GetRequiredService<IServiceGate>().EvaluateAsync(e.OrgId, orden.Id, "routine"))
                .Verdict.Should().Be("Authorized");
        }

        // El segundo clic: ni otra factura ni otro cobro.
        var otra = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.Requisition,
            new LabCheckoutInput(null, null, [new TenderLine("Cash", 400m, null)])));
        otra!.Invoice.Should().BeNull();
        otra.Payment.Should().BeNull();
        otra.State.Invoices.Should().ContainSingle();
    }

    [Fact]
    public async Task FacturarSinCobrar_dejaLaFacturaPendiente_yDespuesSeCobraLaMisma()
    {
        var e = await ArrangeAsync();

        var r = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.Requisition,
            new LabCheckoutInput("Empresa S.A.", "08011999000001", null)));
        r!.Invoice.Should().NotBeNull();
        r.Payment.Should().BeNull();
        r.State.BalanceDue.Should().Be(350m);

        await AbrirTurnoAsync(e);
        var cobro = await ConAsync(h => h.CheckoutAsync(e.OrgId, e.UserId, e.Requisition,
            new LabCheckoutInput(null, null, [new TenderLine("Card", 350m, "VISA-1234")])));
        cobro!.Invoice.Should().BeNull("ya estaba facturada");
        cobro.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);
        cobro.State.Invoices.Should().ContainSingle().Which.Id.Should().Be(r.Invoice!.Id);
        cobro.State.BalanceDue.Should().Be(0m);
    }

    [Fact]
    public async Task SinPrecio_oEnCajaDeFarmacia_oDeConsulta_noFactura_yDiceQueFalta()
    {
        var sinPrecio = await ArrangeAsync(precio: null);
        var estado = await ConAsync(h => h.GetAsync(sinPrecio.OrgId, sinPrecio.UserId, sinPrecio.Requisition));
        estado!.InvoiceBlocker.Should().Contain("Hemograma completo").And.Contain("precio");
        var intento = () => ConAsync(h => h.CheckoutAsync(
            sinPrecio.OrgId, sinPrecio.UserId, sinPrecio.Requisition, new LabCheckoutInput(null, null, null)));
        await intento.Should().ThrowAsync<InvalidOperationException>().WithMessage("*precio*");

        var farmacia = await ArrangeAsync(perfilCaja: "Pharmacy");
        await AbrirTurnoAsync(farmacia);
        (await ConAsync(h => h.GetAsync(farmacia.OrgId, farmacia.UserId, farmacia.Requisition)))!
            .CollectBlocker.Should().Contain("es de farmacia");

        var consulta = await ArrangeAsync(deConsulta: true);
        var deConsulta = await ConAsync(h => h.GetAsync(consulta.OrgId, consulta.UserId, consulta.Requisition));
        deConsulta!.FromConsultation.Should().BeTrue();
        deConsulta.InvoiceBlocker.Should().Contain("se cobra en caja");
    }
}
