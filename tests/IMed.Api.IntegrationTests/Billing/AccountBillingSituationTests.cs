using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Situación de cobro de las cuentas. Observación de staging: la Caja listaba solo cuentas en estado
/// <c>Invoiced</c>, un estado al que ninguna cuenta llegaba nunca, así que una orden que no se cobraba
/// en el acto no se podía cobrar después. La situación ahora se calcula de cargos y facturas.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AccountBillingSituationTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid PartyId, Guid LocationId, Guid FacturaTypeId, Guid ItemId);

    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
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
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "CAI-QA", "002", 1, 500, new DateOnly(2027, 12, 31)));

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(
                orgId, "HEMO", "Hemograma", "Service", exento, 250m,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-sit-1", "María Zelaya", null, "Running"));

        return new Escenario(orgId, cuenta.Id, cuenta.PartyId, sede.Id, factura, item.Id);
    }

    private async Task<IReadOnlyList<BillingAccountDto>> BuscarAsync(
        Escenario e, string? situacion = null, string? texto = null, Guid? sede = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SearchAccountsHandler>().HandleAsync(
            new SearchAccountsQuery(e.OrgId, Situation: situacion, Text: texto, LocationId: sede));
    }

    private async Task CapturarAsync(Escenario e, string origen)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, origen));
    }

    private async Task<InvoiceDto> FacturarAsync(Escenario e)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var r = await scope.ServiceProvider.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        return r.Invoice!;
    }

    [Fact]
    public async Task UnaCuentaSinCargos_estaSinMovimiento_yNoApareceEnPendientes()
    {
        var e = await ArrangeAsync();

        (await BuscarAsync(e)).Should().ContainSingle()
            .Which.Billing!.Situation.Should().Be("Empty");
        (await BuscarAsync(e, "Pending")).Should().BeEmpty();
    }

    [Fact]
    public async Task ConCargosSinFacturar_estaPorFacturar_conSuTotal()
    {
        var e = await ArrangeAsync();
        await CapturarAsync(e, "sr-1");

        var cuenta = (await BuscarAsync(e, "ToInvoice")).Should().ContainSingle().Subject;
        cuenta.Billing!.Situation.Should().Be("ToInvoice");
        cuenta.Billing.PendingToInvoice.Should().Be(250m);
        cuenta.Billing.ToCollect.Should().BeFalse();
        (await BuscarAsync(e, "ToCollect")).Should().BeEmpty("todavía no hay factura que cobrar");
    }

    [Fact]
    public async Task FacturadaSinCobrar_estaPorCobrar_aunqueLaCuentaSigaAbierta()
    {
        // Es el caso de la observación: la recepción factura y el paciente paga después en caja.
        var e = await ArrangeAsync();
        await CapturarAsync(e, "sr-1");
        await FacturarAsync(e);

        var cuenta = (await BuscarAsync(e, "ToCollect")).Should().ContainSingle().Subject;
        cuenta.Status.Should().Be("Open", "el estado guardado no cambia al facturar; la situación sí");
        cuenta.Billing!.Situation.Should().Be("ToCollect");
        cuenta.Billing.BalanceDue.Should().Be(250m);
        cuenta.Billing.OpenInvoices.Should().Be(1);
        (await BuscarAsync(e, "ToInvoice")).Should().BeEmpty();
        (await BuscarAsync(e, "Pending")).Should().ContainSingle();
    }

    [Fact]
    public async Task ConFacturaConSaldoYUnCargoNuevo_estaEnLasDosColas()
    {
        var e = await ArrangeAsync();
        await CapturarAsync(e, "sr-1");
        await FacturarAsync(e);
        await CapturarAsync(e, "sr-2");

        var b = (await BuscarAsync(e)).Should().ContainSingle().Subject.Billing!;
        b.Situation.Should().Be("ToInvoice", "facturar va antes que cobrar");
        b.ToInvoice.Should().BeTrue();
        b.ToCollect.Should().BeTrue();
        (await BuscarAsync(e, "ToInvoice")).Should().ContainSingle();
        (await BuscarAsync(e, "ToCollect")).Should().ContainSingle();
    }

    [Fact]
    public async Task FacturadaYPagada_estaAlDia()
    {
        var e = await ArrangeAsync();
        await CapturarAsync(e, "sr-1");
        var factura = await FacturarAsync(e);

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
                new CreateCashPointCommand(e.OrgId, e.LocationId, "CAJA1", "Caja 1", "002", "Services", false, null));
            var cajero = Guid.NewGuid();
            var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
                .HandleAsync(e.OrgId, caja.Id, cajero, 0m, anyLocation: true);
            var pago = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
                new ReceivePaymentCommand(
                    e.OrgId, turno.Session!.Id, factura.BillToPartyId, cajero,
                    [factura.Id], [new TenderLine("Cash", 250m, null)]));
            pago.Succeeded.Should().BeTrue(pago.Message);
        }

        (await BuscarAsync(e, "Settled")).Should().ContainSingle()
            .Which.Billing!.BalanceDue.Should().Be(0m);
        (await BuscarAsync(e, "Pending")).Should().BeEmpty();
    }

    [Fact]
    public async Task SeEncuentraPorNombreDelCliente_sinImportarMayusculas()
    {
        var e = await ArrangeAsync();

        (await BuscarAsync(e, texto: "zelaya")).Should().ContainSingle();
        (await BuscarAsync(e, texto: "Pérez")).Should().BeEmpty();
        (await BuscarAsync(e, texto: "%")).Should().BeEmpty("el % se busca literal, no es comodín");
    }

    [Fact]
    public async Task ElFiltroDeSede_tomaLasCuentasConCargosEnEsaSede()
    {
        var e = await ArrangeAsync();
        await CapturarAsync(e, "sr-1");

        (await BuscarAsync(e, sede: e.LocationId)).Should().ContainSingle();
        (await BuscarAsync(e, sede: Guid.NewGuid())).Should().BeEmpty();
    }

    [Fact]
    public async Task LaConsultaEnCurso_seMarca_siSePide()
    {
        // D4: la Caja cobra consultas en curso, marcadas, para que el cajero sepa que puede venir más.
        var e = await ArrangeAsync();
        var enCurso = $"enc-{Guid.NewGuid():N}";
        var cerrada = $"enc-{Guid.NewGuid():N}";
        fixture.Hapi.RegisterEncounter(enCurso, "in-progress");
        fixture.Hapi.RegisterEncounter(cerrada, "completed");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            foreach (var (enc, pac) in new[] { (enCurso, "pat-a"), (cerrada, "pat-b") })
            {
                var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
                    new OpenAccountCommand(e.OrgId, null, pac, pac, enc, "Episode"));
                await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
                    new CaptureChargeCommand(e.OrgId, cuenta.Id, e.ItemId, ChargeSourceKind.Encounter, enc));
            }
        }

        using var lectura = fixture.Factory.Services.CreateScope();
        var buscar = lectura.ServiceProvider.GetRequiredService<SearchAccountsHandler>();
        var conMarca = await buscar.HandleAsync(new SearchAccountsQuery(e.OrgId, Situation: "Pending", WithConsultationStatus: true));
        conMarca.Single(c => c.EncounterFhirId == enCurso).Billing!.InConsultation.Should().BeTrue();
        conMarca.Single(c => c.EncounterFhirId == cerrada).Billing!.InConsultation.Should().BeFalse();

        var sinMarca = await buscar.HandleAsync(new SearchAccountsQuery(e.OrgId, Situation: "Pending"));
        sinMarca.Should().OnlyContain(c => c.Billing!.InConsultation == null, "solo se consulta a HAPI si se pide");
    }
}
