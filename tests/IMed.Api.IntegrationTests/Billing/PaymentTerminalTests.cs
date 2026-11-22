using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// POS y cuentas bancarias por sede: el cierre separado por banco.
/// <list type="bullet">
///   <item>Sin terminales dados de alta, todo sigue como antes.</item>
///   <item>Con terminales, tarjeta y transferencia dicen a cuál entraron, y solo a uno válido.</item>
///   <item>El cierre del turno, el corte del día y el reporte de cajas suman por terminal.</item>
/// </list>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PaymentTerminalTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid CashPointId, Guid AccountId, Guid PartyId, Guid LocationId, Guid OtraSedeId,
        Guid FacturaTypeId, Guid ItemId, Guid Cajero, Guid TurnoId);

    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        var otra = TestSedes.Create(db, orgId, "NORTE", "Sede norte", isDefault: false);
        otra.SetFiscalEstablishmentCode("002");
        db.OrganizationLocations.AddRange(sede, otra);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(orgId, sede.Id, factura, "CAI-POS", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));
        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(orgId, "PROC", "Procedimiento", "Service", exento, 500m, "Prepaid", "Global", null, null, null));
        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-pos-1", "Ana López", $"enc-{Guid.NewGuid():N}", "Episode"));
        var parte = await sp.GetRequiredService<IPartyRepository>().FindByPatientAsync(orgId, "pat-pos-1");

        var cajero = Guid.NewGuid();
        var turno = await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(orgId, caja.Id, cajero, 0m);
        turno.Succeeded.Should().BeTrue(turno.Message);

        return new Escenario(orgId, caja.Id, cuenta.Id, parte!.Id, sede.Id, otra.Id, factura, item.Id, cajero, turno.Session!.Id);
    }

    private async Task<T> ConAsync<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<PaymentTerminalDto> AltaAsync(Escenario e, string nombre, string banco, string tipo = "PosTerminal", Guid? sede = null) =>
        ConAsync(sp => sp.GetRequiredService<PaymentTerminalHandlers>().CreateAsync(
            new CreatePaymentTerminalCommand(e.OrgId, sede ?? e.LocationId, nombre, banco, tipo)));

    private int _cargo;

    private async Task<ReceivePaymentResult> CobrarAsync(Escenario e, params TenderLine[] pagos)
    {
        var fuente = $"sr-{++_cargo}";
        return await ConAsync(async sp =>
        {
            var cargo = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
                new CaptureChargeCommand(e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, fuente, 1m));
            cargo.Succeeded.Should().BeTrue(cargo.Rejected);
            var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
                new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
            f.Succeeded.Should().BeTrue(f.Rejected?.Message);
            return await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
                new ReceivePaymentCommand(e.OrgId, e.TurnoId, e.PartyId, e.Cajero, [f.Invoice!.Id], pagos));
        });
    }

    [Fact]
    public async Task SinTerminalesDadosDeAlta_laTarjetaSeCobraComoSiempre()
    {
        var e = await ArrangeAsync();

        var r = await CobrarAsync(e, new TenderLine("Card", 500m, "V-1"));

        r.Succeeded.Should().BeTrue(r.Message);
        var resumen = await ConAsync(sp => sp.GetRequiredService<GetSessionTenderSummaryHandler>().HandleAsync(e.OrgId, e.TurnoId));
        resumen!.Groups.Single(g => g.Method == "Card").ByTerminal.Should().BeEmpty("nadie usa terminales: el cierre se ve como antes");
    }

    [Fact]
    public async Task ConPosEnLaSede_laTarjetaTieneQueDecirEnCualSePaso()
    {
        var e = await ArrangeAsync();
        var bac = await AltaAsync(e, "POS BAC", "BAC Credomatic");

        var sin = await CobrarAsync(e, new TenderLine("Card", 500m, "V-1"));
        sin.Code.Should().Be("falta_terminal");

        var con = await CobrarAsync(e, new TenderLine("Card", 500m, "V-2", bac.Id));
        con.Succeeded.Should().BeTrue(con.Message);
        con.Payments.Single().TerminalId.Should().Be(bac.Id);

        // Sin cuentas bancarias dadas de alta, la transferencia no exige nada.
        (await CobrarAsync(e, new TenderLine("Transfer", 500m, "T-1"))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ElTerminal_tieneQueSerActivo_deLaSedeYDelTipoDelMetodo()
    {
        var e = await ArrangeAsync();
        var bac = await AltaAsync(e, "POS BAC", "BAC Credomatic");
        var cuenta = await AltaAsync(e, "Cuenta Atlántida", "Banco Atlántida", "BankAccount");
        var deLaOtraSede = await AltaAsync(e, "POS Ficohsa", "Ficohsa", sede: e.OtraSedeId);
        var viejo = await AltaAsync(e, "POS viejo", "Banpaís");
        await ConAsync(sp => sp.GetRequiredService<PaymentTerminalHandlers>().SetActiveAsync(e.OrgId, viejo.Id, false));

        (await CobrarAsync(e, new TenderLine("Card", 500m, "V-1", deLaOtraSede.Id))).Code.Should().Be("terminal_invalido");
        (await CobrarAsync(e, new TenderLine("Card", 500m, "V-2", viejo.Id))).Code.Should().Be("terminal_invalido");
        (await CobrarAsync(e, new TenderLine("Card", 500m, "V-3", cuenta.Id))).Code.Should().Be("terminal_invalido");
        (await CobrarAsync(e, new TenderLine("Transfer", 500m, "T-1", bac.Id))).Code.Should().Be("terminal_invalido");
        (await CobrarAsync(e, new TenderLine("Cash", 500m, null, bac.Id))).Code.Should().Be("terminal_no_aplica");
        (await CobrarAsync(e, new TenderLine("Transfer", 500m, "T-2"))).Code.Should().Be("falta_terminal");
        (await CobrarAsync(e, new TenderLine("Transfer", 500m, "T-3", cuenta.Id))).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ElCierre_sumaPorPos_yElReporteDeCajasTambien()
    {
        var e = await ArrangeAsync();
        var bac = await AltaAsync(e, "POS BAC", "BAC Credomatic");
        var ficohsa = await AltaAsync(e, "POS Ficohsa", "Ficohsa");

        (await CobrarAsync(e, new TenderLine("Card", 500m, "V-1", bac.Id))).Succeeded.Should().BeTrue();
        (await CobrarAsync(e, new TenderLine("Card", 500m, "V-2", bac.Id))).Succeeded.Should().BeTrue();
        (await CobrarAsync(e, new TenderLine("Card", 200m, "V-3", ficohsa.Id), new TenderLine("Cash", 300m, null)))
            .Succeeded.Should().BeTrue();

        var resumen = await ConAsync(sp => sp.GetRequiredService<GetSessionTenderSummaryHandler>().HandleAsync(e.OrgId, e.TurnoId));
        var tarjeta = resumen!.Groups.Single(g => g.Method == "Card");
        tarjeta.Total.Should().Be(1200m);
        tarjeta.ByTerminal!.Select(t => (t.Name, t.Count, t.Amount)).Should().BeEquivalentTo(new[]
        {
            ("POS BAC (BAC Credomatic)", 2, 1000m),
            ("POS Ficohsa", 1, 200m),
        });
        tarjeta.Lines.Should().OnlyContain(l => l.TerminalName != null);

        var reporte = await ConAsync(sp => sp.GetRequiredService<GetCashReportHandler>().HandleAsync(e.OrgId, null, null, null, null));
        reporte.TotalsByTerminal!.Should().HaveCount(2);
        reporte.TotalsByTerminal!.Single(t => t.TerminalId == bac.Id).Amount.Should().Be(1000m);
        reporte.Shifts.Single().ByTerminal!.Single(t => t.TerminalId == ficohsa.Id).Amount.Should().Be(200m);

        var corte = await ConAsync(sp => sp.GetRequiredService<GetDailyCashCutHandler>().HandleAsync(e.OrgId, null, null));
        corte.TotalsByTerminal!.Sum(t => t.Amount).Should().Be(1200m);
    }

    [Fact]
    public async Task ElCajero_veLosTerminalesActivosDeLaSedeDeSuTurno()
    {
        var e = await ArrangeAsync();
        var bac = await AltaAsync(e, "POS BAC", "BAC Credomatic");
        var viejo = await AltaAsync(e, "POS viejo", "Banpaís");
        await AltaAsync(e, "POS Ficohsa", "Ficohsa", sede: e.OtraSedeId);
        await ConAsync(sp => sp.GetRequiredService<PaymentTerminalHandlers>().SetActiveAsync(e.OrgId, viejo.Id, false));

        var mios = await ConAsync(sp => sp.GetRequiredService<PaymentTerminalHandlers>().ForMySessionAsync(e.OrgId, e.Cajero));
        mios.Select(t => t.Id).Should().Equal(bac.Id);

        (await ConAsync(sp => sp.GetRequiredService<PaymentTerminalHandlers>().ForMySessionAsync(e.OrgId, Guid.NewGuid())))
            .Should().BeEmpty("sin turno abierto no hay sede");
    }

    [Fact]
    public async Task DosTerminalesConElMismoNombreEnLaSede_noSeDistinguirianEnElCierre()
    {
        var e = await ArrangeAsync();
        await AltaAsync(e, "POS BAC", "BAC Credomatic");

        await FluentActions.Invoking(() => AltaAsync(e, "pos bac", "BAC"))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*Ya hay uno*");
        // En otra sede sí: el cierre es por caja, y cada caja ve los de su sede.
        (await AltaAsync(e, "POS BAC", "BAC Credomatic", sede: e.OtraSedeId)).Should().NotBeNull();
    }
}
