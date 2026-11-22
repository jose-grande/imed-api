using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.Patients;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Vender en la Caja (UX-Cobro-Anticipado F2): el cajero cobra exámenes y procedimientos antes del
/// servicio, sin ir al módulo de laboratorio. La venta crea la orden externa por el mismo camino que
/// la recepción, así que el laboratorio la recibe y nada se cobra dos veces.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CounterSaleTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid UserId, ExternalOrderExam Hemograma, Guid Curacion, Guid ExamenComoCobrable);

    /// <summary>Hemograma a L 350 (panel del catálogo) y curación a L 150 (procedimiento), con caja abierta.</summary>
    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(orgId);

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
            new RegisterFiscalAuthorizationCommand(orgId, sede.Id, factura, "CAI-VTA", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));
        (await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(orgId, caja.Id, userId, 0m, anyLocation: true))
            .Succeeded.Should().BeTrue();

        var panel = (await sp.GetRequiredService<ILabCatalogRepository>().ListPanelsAsync(orgId)).First(x => x.Code == "CBC");
        var cobrableCbc = await sp.GetRequiredService<IBillingCatalogRepository>().FindAnyByLabAsync(orgId, null, panel.Id);
        await sp.GetRequiredService<UpdateBillableItemHandler>().HandleAsync(new UpdateBillableItemCommand(
            orgId, cobrableCbc!.Id, "Hemograma completo", exento, 350m, "Prepaid", Active: true));

        var curacion = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(new CreateBillableItemCommand(
            orgId, "CUR", "Curación simple", "Service", exento, 150m, "Either", "Global", null, null, null));

        var hemograma = new ExternalOrderExam(panel.LoincCode, panel.Code, LabOrderCodeSystems.Panel, panel.Name, panel.Name);
        return new Escenario(orgId, userId, hemograma, curacion.Id, cobrableCbc.Id);
    }

    private async Task<T> ConAsync<T>(Func<CounterSaleHandler, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider.GetRequiredService<CounterSaleHandler>());
    }

    [Fact]
    public async Task ElBuscador_traeExamenesPanelesYProcedimientos_conSuPrecio_yNoLasConsultas()
    {
        // El cajero no tiene permisos de laboratorio: el buscador de la venta es propio.
        var e = await ArrangeAsync();

        var hemo = await ConAsync(h => h.SearchAsync(e.OrgId, "hemograma"));
        var panel = hemo.Should().ContainSingle(x => x.Kind == "panel").Subject;
        panel.UnitPrice.Should().Be(350m);
        panel.Exam!.LocalSystem.Should().Be(LabOrderCodeSystems.Panel);

        var cur = await ConAsync(h => h.SearchAsync(e.OrgId, "curaci"));
        cur.Should().ContainSingle(x => x.Kind == "procedure").Which.UnitPrice.Should().Be(150m);

        (await ConAsync(h => h.SearchAsync(e.OrgId, "h"))).Should().BeEmpty("con menos de dos letras no se busca");
    }

    [Fact]
    public async Task LaCotizacion_daElPrecioDeCadaLinea_sinCrearNada()
    {
        var e = await ArrangeAsync();
        var ordenesAntes = fixture.Hapi.ServiceRequestCount;

        var q = await ConAsync(h => h.QuoteAsync(e.OrgId, [e.Hemograma], [new CounterSaleProcedure(e.Curacion, 2)]));

        q.Lines.Select(l => (l.Kind, l.Total)).Should().Equal(("panel", 350m), ("procedure", 300m));
        q.Total.Should().Be(650m);
        q.CanSell.Should().BeTrue();
        q.AnyPricePending.Should().BeFalse();
        fixture.Hapi.ServiceRequestCount.Should().Be(ordenesAntes, "cotizar no crea la orden");
    }

    [Fact]
    public async Task Vender_creaLaOrdenAdmitida_cargaTodoEnSuCuenta_yLaCajaLaCobra()
    {
        var e = await ArrangeAsync();

        var venta = await ConAsync(h => h.CreateAsync(e.OrgId, e.UserId, new CounterSaleInput(
            "pat-vta-1", "María Zelaya", [e.Hemograma], [new CounterSaleProcedure(e.Curacion)])));

        venta.Requisition.Should().NotBeNullOrEmpty("los exámenes van por una orden de laboratorio");
        venta.ServiceRequestIds.Should().ContainSingle();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var cuenta = await sp.GetRequiredService<IBillingAccountRepository>().GetAccountAsync(e.OrgId, venta.AccountId);
            cuenta!.Origin.Should().Be(BillingAccountOrigin.LabOrder, "es la cuenta de la orden, como en la recepción");
            cuenta.OriginRef.Should().Be(venta.Requisition);
            var cargos = await sp.GetRequiredService<IBillingAccountRepository>().ListChargesAsync(e.OrgId, venta.AccountId);
            cargos.Should().HaveCount(2, "el hemograma (al admitir) y la curación");
            cargos.Where(c => c.SourceKind == ChargeSourceKind.LabOrder).Should().ContainSingle(
                "el examen lo cobra la admisión de la orden, una sola vez");
        }

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var cobro = await scope.ServiceProvider.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
                e.OrgId, e.UserId, venta.AccountId, new AccountCheckoutInput(null, null, [new TenderLine("Cash", 500m, null)]));
            cobro!.Invoice!.GrandTotal.Should().Be(500m);
            cobro.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);
        }

        // El laboratorio la recibe pagada: la puerta de prepago la autoriza.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var orden = (await sp.GetRequiredService<IHapiFhirClient>()
                .SearchLabOrdersByRequisitionAsync(venta.Requisition!, $"org-{e.OrgId}")).Single();
            (await sp.GetRequiredService<IServiceGate>().EvaluateAsync(e.OrgId, orden.Id, "routine"))
                .Verdict.Should().Be("Authorized");
        }
    }

    [Fact]
    public async Task SoloProcedimientos_vanALaCuentaCorrienteDelPaciente()
    {
        var e = await ArrangeAsync();
        var ordenesAntes = fixture.Hapi.ServiceRequestCount;

        var venta = await ConAsync(h => h.CreateAsync(e.OrgId, e.UserId, new CounterSaleInput(
            "pat-vta-2", "José Martínez", null, [new CounterSaleProcedure(e.Curacion)])));

        venta.Requisition.Should().BeNull();
        using var scope = fixture.Factory.Services.CreateScope();
        var cuenta = await scope.ServiceProvider.GetRequiredService<IBillingAccountRepository>().GetAccountAsync(e.OrgId, venta.AccountId);
        cuenta!.Kind.Should().Be(BillingAccountKind.Running);
        fixture.Hapi.ServiceRequestCount.Should().Be(ordenesAntes, "sin exámenes no hay orden de laboratorio");
    }

    [Fact]
    public async Task UnExamenComoProcedimiento_seRechaza_antesDeCrearLaOrden()
    {
        // Cargarlo como procedimiento lo cobraría sin que el laboratorio se entere.
        var e = await ArrangeAsync();
        var ordenesAntes = fixture.Hapi.ServiceRequestCount;

        var vender = () => ConAsync(h => h.CreateAsync(e.OrgId, e.UserId, new CounterSaleInput(
            "pat-vta-3", "Ana", [e.Hemograma], [new CounterSaleProcedure(e.ExamenComoCobrable)])));

        await vender.Should().ThrowAsync<ArgumentException>().WithMessage("*es un examen*");
        fixture.Hapi.ServiceRequestCount.Should().Be(ordenesAntes, "se valida antes de crear nada en el laboratorio");
    }

    [Fact]
    public async Task SinPacienteOSinNada_noSeVende()
    {
        var e = await ArrangeAsync();

        await FluentActions.Invoking(() => ConAsync(h => h.CreateAsync(e.OrgId, e.UserId,
                new CounterSaleInput(" ", null, [e.Hemograma], null))))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*paciente*");
        await FluentActions.Invoking(() => ConAsync(h => h.CreateAsync(e.OrgId, e.UserId,
                new CounterSaleInput("pat-vta-4", "Ana", [], []))))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*al menos*");
    }

    [Fact]
    public async Task ElAltaRapida_creaElPaciente_yNoLoDuplica()
    {
        // D6: identidad, nombre y fecha de nacimiento obligatorios; la misma identidad devuelve al mismo paciente.
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var alta = scope.ServiceProvider.GetRequiredService<QuickRegisterPatientHandler>();
        QuickRegisterPatientCommand Comando(string identidad, string nombres = "María José", DateOnly? nacimiento = null) =>
            new(orgId, "HN", "IdentidadNacional", identidad, nombres, "Zelaya Pérez", nacimiento ?? new DateOnly(1990, 5, 12));

        var primera = await alta.HandleAsync(Comando("0801-1990-12345"));
        primera.Created.Should().BeTrue();
        primera.DisplayName.Should().Be("María José Zelaya Pérez");

        var otraVez = await alta.HandleAsync(Comando("0801199012345", nombres: "Otra Persona"));
        otraVez.Created.Should().BeFalse("la misma identidad, escrita sin guiones, es el mismo paciente");
        otraVez.PatientFhirId.Should().Be(primera.PatientFhirId);
        otraVez.DisplayName.Should().Be("María José Zelaya Pérez", "se devuelve el nombre registrado, no el escrito");

        await FluentActions.Invoking(() => alta.HandleAsync(Comando("123")))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Identidad*");
        await FluentActions.Invoking(() => alta.HandleAsync(Comando("0801-1990-54321", nombres: " ")))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*nombres*");
        await FluentActions.Invoking(() => alta.HandleAsync(Comando("0801-1990-54321", nacimiento: new DateOnly(2999, 1, 1))))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*nacimiento*");
    }
}
