using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Caja: turno, cobro y arqueo (FAC-6).
///
/// <para>El arqueo a ciegas se prueba de una forma poco habitual: verificando que un dato
/// <b>no</b> esté. Es lo único que puede fallar en silencio — el día que alguien agregue el
/// esperado a la respuesta del turno abierto "para agilizar", nada más se rompe.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CashRegisterTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid CashPointId, Guid AccountId, Guid PartyId,
        Guid LocationId, Guid FacturaTypeId, Guid ItemId);

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

        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(
                orgId, "CONS_GEN", "Consulta general", "Service", exento, 500m,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(
                orgId, null, "pat-caja-1", "Juan Pérez", $"enc-{Guid.NewGuid():N}", "Episode"));

        var parte = await sp.GetRequiredService<IPartyRepository>()
            .FindByPatientAsync(orgId, "pat-caja-1");

        return new Escenario(orgId, caja.Id, cuenta.Id, parte!.Id, sede.Id, factura, item.Id);
    }

    /// <summary>Cobra un cargo y emite la factura. Devuelve su id y su total.</summary>
    private static async Task<(Guid Id, decimal Total)> FacturarAsync(
        IServiceProvider sp, Escenario e, string sourceId, decimal cantidad = 1m)
    {
        var cargo = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ItemId, ChargeSourceKind.LabOrder, sourceId, cantidad));
        cargo.Succeeded.Should().BeTrue(cargo.Rejected);

        var factura = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        factura.Succeeded.Should().BeTrue(factura.Rejected?.Message);

        return (factura.Invoice!.Id, factura.Invoice.GrandTotal);
    }

    private static async Task<Guid> AbrirTurnoAsync(
        IServiceProvider sp, Escenario e, Guid cajero, decimal inicial = 1000m)
    {
        var r = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, cajero, inicial);
        r.Succeeded.Should().BeTrue(r.Message);
        return r.Session!.Id;
    }

    [Fact]
    public async Task ElArqueo_noRevelaElEsperadoAntesDelConteo()
    {
        // 🔴 Criterio de aceptación 4, y la decisión que hay que defender cuando alguien pida
        // "mostrarlo para agilizar": si el número está a la vista, lo que se registra es ese
        // número y no lo que hay en la gaveta.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);

        var f = await FacturarAsync(sp, e, "sr-1");
        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Cash", f.Total, null)]));

        var abierto = await sp.GetRequiredService<GetOpenSessionHandler>()
            .HandleAsync(e.OrgId, cajero);

        abierto!.Session.ExpectedAmount.Should().BeNull(
            "el turno abierto no puede exponer el efectivo esperado");
        abierto.Session.CountedAmount.Should().BeNull();
        abierto.Session.Difference.Should().BeNull();
        abierto.PaymentCount.Should().Be(1, "sí puede decir cuántos cobros lleva");
    }

    [Fact]
    public async Task AlCerrar_reciénAhiApareceElEsperado()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);

        var f = await FacturarAsync(sp, e, "sr-1");
        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Cash", 500m, null)]));

        var cerrado = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 1500m, null, null));

        cerrado.Succeeded.Should().BeTrue(cerrado.Message);
        cerrado.Session!.ExpectedAmount.Should().Be(1500m, "1000 inicial + 500 cobrados en efectivo");
        cerrado.Session.CountedAmount.Should().Be(1500m);
        cerrado.Session.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task UnPagoMixto_seRepartiteYCalculaElVuelto()
    {
        // Criterio de aceptación 2. "Parte en efectivo, parte con tarjeta" es lo normal cuando el
        // paciente no llega al total con lo que trae.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1"); // 500

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
            [
                new TenderLine("Card", 300m, "VOUCHER-123"),
                new TenderLine("Cash", 300m, null),
            ]));

        r.Succeeded.Should().BeTrue(r.Message);
        r.Applied.Should().Be(500m);
        r.ChangeDue.Should().Be(100m, "entregó 600 por una factura de 500");
        r.Payments.Should().HaveCount(2, "cada método es un pago propio: concilian por separado");
    }

    [Fact]
    public async Task ElVueltoSaleDelEfectivo_noDeLaTarjeta()
    {
        // De una tarjeta no se devuelve dinero. Si sobra y no hubo efectivo, alguien tecleó mal el
        // monto — y confirmarlo dejaría a la caja debiendo un vuelto que no puede dar.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1"); // 500

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Card", 700m, "VOUCHER-1")]));

        r.Succeeded.Should().BeFalse();
        r.Code.Should().Be("vuelto_sin_efectivo");
        r.Message.Should().Contain("tarjeta");
    }

    [Fact]
    public async Task UnPagoQueCubreDosFacturas_seDistribuyeBien()
    {
        // Criterio de aceptación 3. Modelar el pago 1:1 con la factura parece más simple y se paga
        // caro acá: el paciente que paga tres consultas de la familia con una tarjeta no entra.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);

        var a = await FacturarAsync(sp, e, "sr-a"); // 500
        var b = await FacturarAsync(sp, e, "sr-b"); // 500

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [a.Id, b.Id],
                [new TenderLine("Cash", 1000m, null)]));

        r.Succeeded.Should().BeTrue(r.Message);
        r.Applied.Should().Be(1000m);
        r.ChangeDue.Should().Be(0m);

        var pago = r.Payments.Single();
        pago.Allocations.Should().HaveCount(2);
        pago.Allocations.Sum(x => x.Amount).Should().Be(1000m);

        // Y las dos facturas quedan pagadas.
        var leer = sp.GetRequiredService<GetInvoiceHandler>();
        (await leer.HandleAsync(e.OrgId, a.Id))!.Status.Should().Be("Paid");
        (await leer.HandleAsync(e.OrgId, b.Id))!.Status.Should().Be("Paid");
    }

    [Fact]
    public async Task UnPagoParcial_dejaLaFacturaConSaldo()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1"); // 500

        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Cash", 200m, null)]));

        var factura = await sp.GetRequiredService<GetInvoiceHandler>().HandleAsync(e.OrgId, f.Id);

        factura!.Status.Should().Be("PartiallyPaid");
        factura.Balance.Should().Be(300m);
    }

    [Fact]
    public async Task UnaTarjetaSinVoucher_seRechaza()
    {
        // Sin referencia no se puede conciliar contra el estado de cuenta del banco, y el faltante
        // aparece a fin de mes sin forma de rastrearlo.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1");

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Card", 500m, null)]));

        r.Succeeded.Should().BeFalse();
        r.Message.Should().Contain("referencia");
    }

    [Fact]
    public async Task LaTarjeta_noEntraAlEfectivoDelArqueo()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);
        var f = await FacturarAsync(sp, e, "sr-1");

        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Card", 500m, "VOUCHER-9")]));

        var cerrado = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 1000m, null, null));

        cerrado.Session!.ExpectedAmount.Should().Be(1000m,
            "la tarjeta no toca la gaveta: en efectivo sigue estando solo el monto inicial");
        cerrado.Session.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task UnRetiro_bajaElEfectivoEsperado()
    {
        // Una caja no debe acumular L 200,000. Sin registrar el retiro, el arqueo daría un
        // faltante enorme que nadie puede explicar.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);

        await sp.GetRequiredService<RegisterCashMovementHandler>().HandleAsync(
            new RegisterMovementCommand(
                e.OrgId, turnoId, "Withdrawal", 600m, "Retiro a bóveda", cajero, null));

        var cerrado = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 400m, null, null));

        cerrado.Session!.ExpectedAmount.Should().Be(400m);
        cerrado.Session.Difference.Should().Be(0m);
    }

    [Fact]
    public async Task UnMovimientoSinMotivo_seRechaza()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);

        var acto = async () => await sp.GetRequiredService<RegisterCashMovementHandler>()
            .HandleAsync(new RegisterMovementCommand(
                e.OrgId, turnoId, "Withdrawal", 100m, "  ", cajero, null));

        await acto.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task UnaDiferenciaSinExplicar_noCierraElTurno()
    {
        // Al día siguiente ya nadie recuerda si faltaron 200 por un vuelto mal dado o por un
        // billete falso.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);

        var r = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 900m, null, null));

        r.Succeeded.Should().BeFalse();
        r.Code.Should().Be("diferencia_sin_explicar");
    }

    [Fact]
    public async Task UnaDiferenciaSobreElTope_exigeSupervisor()
    {
        // Criterio de aceptación 6 aplicado al arqueo: un faltante grande no se cierra solo.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();

        await sp.GetRequiredService<IAuthorizationLimitRepository>().AddAsync(
            UserAuthorizationLimit.Create(
                e.OrgId, cajero, AuthorizationLimitKind.CashCountDifference, 50m,
                new DateOnly(2026, 1, 1)));

        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);
        var cerrar = sp.GetRequiredService<CloseCashSessionHandler>();

        var sinAutorizar = await cerrar.HandleAsync(new CloseCashSessionCommand(
            e.OrgId, turnoId, cajero, 800m, "Faltó dinero", null));

        sinAutorizar.Succeeded.Should().BeFalse();
        sinAutorizar.Code.Should().Be("diferencia_sobre_tope");
        sinAutorizar.Message.Should().Contain("supervisor");

        var supervisor = Guid.NewGuid();
        var autorizado = await cerrar.HandleAsync(new CloseCashSessionCommand(
            e.OrgId, turnoId, cajero, 800m, "Faltó dinero, revisado con el jefe", supervisor));

        autorizado.Succeeded.Should().BeTrue(autorizado.Message);
        autorizado.Session!.DifferenceAuthorizedBy.Should().Be(supervisor);
        autorizado.Session.Difference.Should().Be(-200m);
    }

    [Fact]
    public async Task UnaDiferenciaChica_cierraSinSupervisor()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();

        await sp.GetRequiredService<IAuthorizationLimitRepository>().AddAsync(
            UserAuthorizationLimit.Create(
                e.OrgId, cajero, AuthorizationLimitKind.CashCountDifference, 50m,
                new DateOnly(2026, 1, 1)));

        var turnoId = await AbrirTurnoAsync(sp, e, cajero, inicial: 1000m);

        var r = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 980m, "Vuelto mal dado", null));

        r.Succeeded.Should().BeTrue(r.Message);
        r.Session!.Difference.Should().Be(-20m);
        r.Session.DifferenceAuthorizedBy.Should().BeNull();
    }

    [Fact]
    public async Task ElTraspaso_dejaDosTurnosCerrablesPorSeparado()
    {
        // Criterio de aceptación 5. El entrante arranca con lo que el saliente dejó contado:
        // contar de nuevo la misma gaveta puede dar dos números distintos.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var saliente = Guid.NewGuid();
        var entrante = Guid.NewGuid();

        var turnoA = await AbrirTurnoAsync(sp, e, saliente, inicial: 1000m);
        var f = await FacturarAsync(sp, e, "sr-1");
        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoA, e.PartyId, saliente, [f.Id],
                [new TenderLine("Cash", 500m, null)]));

        var traspaso = await sp.GetRequiredService<HandoverCashSessionHandler>().HandleAsync(
            new HandoverCommand(e.OrgId, turnoA, saliente, entrante, 1500m, null, null));

        traspaso.Succeeded.Should().BeTrue(traspaso.Message);
        traspaso.Closed!.Status.Should().Be("Closed");
        traspaso.Closed.Difference.Should().Be(0m);
        traspaso.Opened!.Status.Should().Be("Open");
        traspaso.Opened.OpeningAmount.Should().Be(1500m, "arranca con lo que el saliente dejó");
        traspaso.Opened.PreviousSessionId.Should().Be(turnoA);

        // Y el turno del entrante se cierra por su cuenta.
        var cierre = await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, traspaso.Opened.Id, entrante, 1500m, null, null));

        cierre.Succeeded.Should().BeTrue(cierre.Message);
        cierre.Session!.ExpectedAmount.Should().Be(1500m, "no hereda los cobros del turno anterior");
    }

    [Fact]
    public async Task DosTurnosAbiertosEnLaMismaCaja_seRechaza()
    {
        // El arqueo de cada uno reclamaría el efectivo del otro.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await AbrirTurnoAsync(sp, e, Guid.NewGuid());

        var r = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, Guid.NewGuid(), 500m);

        r.Succeeded.Should().BeFalse();
        r.Code.Should().Be("turno_abierto");
        r.Message.Should().Contain("traspaso");
    }

    [Fact]
    public async Task CobrarConElTurnoCerrado_seRechaza()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1");

        await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turnoId, cajero, 1000m, null, null));

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Cash", 500m, null)]));

        r.Succeeded.Should().BeFalse();
        r.Code.Should().Be("turno_cerrado");
    }

    [Fact]
    public async Task UnaFacturaAnulada_noSeCobra()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var turnoId = await AbrirTurnoAsync(sp, e, cajero);
        var f = await FacturarAsync(sp, e, "sr-1");

        await sp.GetRequiredService<VoidInvoiceHandler>()
            .HandleAsync(e.OrgId, f.Id, "Error de captura");

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(e.OrgId, turnoId, e.PartyId, cajero, [f.Id],
                [new TenderLine("Cash", 500m, null)]));

        r.Succeeded.Should().BeFalse();
        r.Code.Should().Be("factura_no_cobrable");
    }
}
