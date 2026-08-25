using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Gate;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Puerta de prepago (FAC-7).
///
/// <para>Las tres reglas del plan no son configurables y por eso tienen prueba: la emergencia
/// siempre pasa, el estado se muestra pero no bloquea, y quien libera queda registrado.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class ServiceGateTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid PartyId, Guid LocationId,
        Guid FacturaTypeId, Guid PrepagoId, Guid PostpagoId);

    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = OrganizationLocation.Create(orgId, "CENTRAL", "Sede central", isDefault: true);
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

        var items = sp.GetRequiredService<CreateBillableItemHandler>();
        var prepago = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "LAB_CBC", "Hemograma", "Service", exento, 350m,
            "Prepaid", "Global", null, null, null));
        var postpago = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "CONS_URG", "Consulta de urgencia", "Service", exento, 800m,
            "Postpaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(
                orgId, null, "pat-gate-1", "Juan Pérez", $"enc-{Guid.NewGuid():N}", "Episode"));

        var parte = await sp.GetRequiredService<IPartyRepository>()
            .FindByPatientAsync(orgId, "pat-gate-1");

        return new Escenario(
            orgId, cuenta.Id, parte!.Id, sede.Id, factura, prepago.Id, postpago.Id);
    }

    private static async Task CobrarCargoAsync(
        IServiceProvider sp, Escenario e, Guid itemId, string sourceId)
    {
        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, itemId, ChargeSourceKind.LabOrder, sourceId));
        r.Succeeded.Should().BeTrue(r.Rejected);
    }

    private static Task<GateDecisionDto> PreguntarAsync(
        IServiceProvider sp, Escenario e, string sourceId, string? priority = null) =>
        sp.GetRequiredService<EvaluateServiceGateHandler>().HandleAsync(
            new GateQuery(
                e.OrgId, $"ServiceRequest/{sourceId}", sourceId,
                ChargeSourceKind.LabOrder, priority));

    [Fact]
    public async Task UnItemPostpago_noSeBloqueaNunca()
    {
        // Criterio de aceptación 2.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PostpagoId, "sr-post");

        var d = await PreguntarAsync(sp, e, "sr-post");

        d.Verdict.Should().Be("Authorized");
        d.Reason.Should().Be("Postpaid");
    }

    [Theory]
    [InlineData("stat")]
    [InlineData("urgent")]
    [InlineData("STAT")]
    [InlineData("asap")]
    public async Task LaEmergencia_pasaSiempre(string prioridad)
    {
        // 🔴 Criterio de aceptación 3. Es criterio clínico, no configuración: no hay pantalla
        // donde la clínica pueda decidir que un `stat` espere a pagar.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-urgente");

        var d = await PreguntarAsync(sp, e, "sr-urgente", prioridad);

        d.Verdict.Should().Be("Authorized");
        d.Reason.Should().Be("ClinicalUrgency");
    }

    [Fact]
    public async Task LaEmergencia_pasaAunSinCargoNiCuenta()
    {
        // El caso que importa: el paciente que llega grave no tiene cuenta abierta todavía.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();

        var d = await PreguntarAsync(
            scope.ServiceProvider, e, "sr-nunca-visto", "stat");

        d.Verdict.Should().Be("Authorized");
    }

    [Fact]
    public async Task UnPrepagoSinCobrar_quedaPendiente_peroNoProhibido()
    {
        // 🔴 Regla 1: el estado se muestra, no se impone. La respuesta informa cuánto falta; lo
        // que el LIS haga con eso es decisión del LIS.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-impago");

        var d = await PreguntarAsync(sp, e, "sr-impago");

        d.Verdict.Should().Be("PendingPayment");
        d.AmountDue.Should().Be(350m);
        d.Message.Should().Contain("350");
    }

    [Fact]
    public async Task UnServicioSinCargo_seDistingueDeUnoImpago()
    {
        // No es lo mismo "falta pagar" que "esto no está en el catálogo cobrable": lo segundo es
        // deuda de configuración y alguien tiene que verlo.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();

        var d = await PreguntarAsync(scope.ServiceProvider, e, "sr-sin-cargo");

        d.Verdict.Should().Be("PendingPayment");
        d.Reason.Should().Be("NotCharged");
    }

    [Fact]
    public async Task UnaVezCobrado_laPuertaLoAutoriza()
    {
        // 🎯 Criterio de aceptación 1, el corazón del flujo A.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-flujo-a");

        (await PreguntarAsync(sp, e, "sr-flujo-a")).Verdict
            .Should().Be("PendingPayment", "todavía no se cobró");

        // Facturar y cobrar.
        var factura = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        factura.Succeeded.Should().BeTrue(factura.Rejected?.Message);

        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(
                e.OrgId, e.LocationId, "CAJA1", "Caja 1", "002", "Services", false, null));
        var cajero = Guid.NewGuid();
        var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, caja.Id, cajero, 0m);

        var cobro = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(
                e.OrgId, turno.Session!.Id, e.PartyId, cajero, [factura.Invoice!.Id],
                [new TenderLine("Cash", factura.Invoice.GrandTotal, null)]));
        cobro.Succeeded.Should().BeTrue(cobro.Message);

        var d = await PreguntarAsync(sp, e, "sr-flujo-a");

        d.Verdict.Should().Be("Authorized");
        d.Reason.Should().Be("Paid");
    }

    [Fact]
    public async Task UnPagoParcial_dejaLaPuertaPendienteConElSaldo()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-parcial");

        var factura = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(
                e.OrgId, e.LocationId, "CAJA1", "Caja 1", "002", "Services", false, null));
        var cajero = Guid.NewGuid();
        var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, caja.Id, cajero, 0m);

        await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(
            new ReceivePaymentCommand(
                e.OrgId, turno.Session!.Id, e.PartyId, cajero, [factura.Invoice!.Id],
                [new TenderLine("Cash", 100m, null)]));

        var d = await PreguntarAsync(sp, e, "sr-parcial");

        d.Verdict.Should().Be("PendingPayment");
        d.AmountDue.Should().Be(250m, "faltan 250 de los 350");
    }

    [Fact]
    public async Task AnularLaFactura_devuelveLaPuertaAPendiente()
    {
        // Si la factura se anula, lo que la respaldaba dejó de valer.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-anulada");

        var factura = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        await sp.GetRequiredService<VoidInvoiceHandler>()
            .HandleAsync(e.OrgId, factura.Invoice!.Id, "Error de captura");

        var d = await PreguntarAsync(sp, e, "sr-anulada");

        d.Verdict.Should().Be("PendingPayment");
    }

    [Fact]
    public async Task LiberarUnServicio_quedaConUsuarioMotivoYFecha()
    {
        // Criterio de aceptación 4. Convierte la excepción en un dato medible.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-liberado");

        var supervisor = Guid.NewGuid();
        var waiver = await sp.GetRequiredService<WaiveServiceHandler>().HandleAsync(
            new WaiveServiceCommand(
                e.OrgId, "ServiceRequest/sr-liberado", supervisor,
                "Paciente de la casa, autorizado por el dueño", e.AccountId));

        waiver.WaivedBy.Should().Be(supervisor);
        waiver.Reason.Should().Contain("autorizado por el dueño");
        waiver.WaivedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));

        var d = await PreguntarAsync(sp, e, "sr-liberado");
        d.Verdict.Should().Be("Waived");
        d.WaivedBy.Should().Be(supervisor);
        d.Message.Should().Contain("autorizado por el dueño");
    }

    [Fact]
    public async Task LiberarSinMotivo_seRechaza()
    {
        // Sin motivo la constancia no sirve para el reporte: "se liberó" sin más es lo mismo que
        // no haber registrado nada.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();

        var acto = async () => await scope.ServiceProvider.GetRequiredService<WaiveServiceHandler>()
            .HandleAsync(new WaiveServiceCommand(
                e.OrgId, "ServiceRequest/sr-x", Guid.NewGuid(), "  ", null));

        await acto.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LiberarDosVeces_noDuplicaLaConstancia()
    {
        // El técnico que aprieta el botón otra vez porque la pantalla tardó no debería generar
        // dos registros de excepción sobre el mismo acto.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var handler = sp.GetRequiredService<WaiveServiceHandler>();
        var cmd = new WaiveServiceCommand(
            e.OrgId, "ServiceRequest/sr-doble", Guid.NewGuid(), "Cortesía", null);

        var a = await handler.HandleAsync(cmd);
        var b = await handler.HandleAsync(cmd);

        b.Id.Should().Be(a.Id);

        var listado = await sp.GetRequiredService<ListServiceWaiversHandler>()
            .HandleAsync(e.OrgId, null, null);
        listado.Should().ContainSingle();
    }

    [Fact]
    public async Task ElReporteDeExcepciones_listaLoQueSeDioSinCobrar()
    {
        // "Cuántos servicios se dieron sin cobrar y quién los autorizó" es lo que el dueño va a
        // pedir el primer mes.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var handler = sp.GetRequiredService<WaiveServiceHandler>();

        await handler.HandleAsync(new WaiveServiceCommand(
            e.OrgId, "ServiceRequest/sr-1", Guid.NewGuid(), "Cortesía", null));
        await handler.HandleAsync(new WaiveServiceCommand(
            e.OrgId, "ServiceRequest/sr-2", Guid.NewGuid(), "Empleado", null));

        var listado = await sp.GetRequiredService<ListServiceWaiversHandler>()
            .HandleAsync(e.OrgId, null, null);

        listado.Should().HaveCount(2);
        listado.Select(w => w.Reason).Should().Contain(["Cortesía", "Empleado"]);
    }

    [Fact]
    public async Task LaExoneracion_ganaSobreElEstadoDelCobro()
    {
        // Preguntar por el pago antes que por la exoneración mostraría "pendiente" sobre algo que
        // un supervisor ya resolvió.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarCargoAsync(sp, e, e.PrepagoId, "sr-orden");

        (await PreguntarAsync(sp, e, "sr-orden")).Verdict.Should().Be("PendingPayment");

        await sp.GetRequiredService<WaiveServiceHandler>().HandleAsync(new WaiveServiceCommand(
            e.OrgId, "ServiceRequest/sr-orden", Guid.NewGuid(), "Caso social", null));

        (await PreguntarAsync(sp, e, "sr-orden")).Verdict.Should().Be("Waived");
    }

    [Fact]
    public async Task LaPuertaDeOtraOrganizacion_noVeNuestrasLiberaciones()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<WaiveServiceHandler>().HandleAsync(new WaiveServiceCommand(
            e.OrgId, "ServiceRequest/sr-1", Guid.NewGuid(), "Cortesía", null));

        var listado = await sp.GetRequiredService<ListServiceWaiversHandler>()
            .HandleAsync(Guid.NewGuid(), null, null);

        listado.Should().BeEmpty();
    }
}
