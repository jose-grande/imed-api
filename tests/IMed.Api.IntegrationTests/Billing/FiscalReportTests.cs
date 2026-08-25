using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Reportes de cierre fiscal (FAC-8). Es lo que separa "lo vimos funcionar" de "lo pueden usar el
/// lunes": el contador no puede declarar sin el reporte de ISV, y una auditoría pregunta por los
/// huecos de correlativo.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class FiscalReportTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid PartyId, Guid LocationId,
        Guid FacturaTypeId, Guid AuthId, Guid CashPointId, Guid ExentoId, Guid GravadoId);

    private async Task<Escenario> ArrangeAsync(long rangeTo = 500)
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
        var isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;

        var auth = await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "CAI-QA", "002", 1, rangeTo, new DateOnly(2027, 12, 31)));

        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));

        var items = sp.GetRequiredService<CreateBillableItemHandler>();
        var consulta = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "CONS", "Consulta general", "Service", exento, 500m, "Prepaid", "Global", null, null, null));
        var estetica = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "EST", "Toxina botulínica", "Service", isv15, 4000m, "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-rep-1", "Juan Pérez", $"enc-{Guid.NewGuid():N}", "Episode"));
        var parte = await sp.GetRequiredService<IPartyRepository>()
            .FindByPatientAsync(orgId, "pat-rep-1");

        return new Escenario(
            orgId, cuenta.Id, parte!.Id, sede.Id, factura, auth.Id, caja.Id, consulta.Id, estetica.Id);
    }

    private static async Task<Guid> FacturarAsync(
        IServiceProvider sp, Escenario e, Guid itemId, string sourceId, decimal cantidad = 1m)
    {
        var cargo = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, itemId, ChargeSourceKind.LabOrder, sourceId, cantidad));
        cargo.Succeeded.Should().BeTrue(cargo.Rejected);

        var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        f.Succeeded.Should().BeTrue(f.Rejected?.Message);
        return f.Invoice!.Id;
    }

    // ── Reporte de ISV ──────────────────────────────────────────────────────

    [Fact]
    public async Task ElReporteDeIsv_separaLosTratamientos()
    {
        // Es lo que el contador necesita para declarar: exento y cada tasa gravada van en
        // renglones distintos. Un total único lo obliga a recalcular a mano.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await FacturarAsync(sp, e, e.ExentoId, "sr-1");
        await FacturarAsync(sp, e, e.GravadoId, "sr-2");

        var r = await sp.GetRequiredService<GetTaxReportHandler>().HandleAsync(e.OrgId, null, null);

        r.DocumentCount.Should().Be(2);
        r.SubtotalExempt.Should().Be(500m);
        r.SubtotalTaxed15.Should().Be(4000m);
        r.SubtotalTaxed18.Should().Be(0m);
        r.TaxTotal.Should().Be(600m);
        r.GrandTotal.Should().Be(5100m);
    }

    [Fact]
    public async Task LoAnulado_noEntraAlReporteDeIsv()
    {
        // Un documento anulado no generó obligación tributaria: sumarlo declararía de más.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var a = await FacturarAsync(sp, e, e.ExentoId, "sr-1");
        await FacturarAsync(sp, e, e.GravadoId, "sr-2");

        await sp.GetRequiredService<VoidInvoiceHandler>()
            .HandleAsync(e.OrgId, a, "Error de captura");

        var r = await sp.GetRequiredService<GetTaxReportHandler>().HandleAsync(e.OrgId, null, null);

        r.DocumentCount.Should().Be(1);
        r.VoidedCount.Should().Be(1, "pero se reporta cuántos hubo");
        r.SubtotalExempt.Should().Be(0m);
        r.GrandTotal.Should().Be(4600m);
    }

    // ── Libro de documentos y huecos ────────────────────────────────────────

    [Fact]
    public async Task ElLibro_listaLosDocumentosDeUnaAutorizacion()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await FacturarAsync(sp, e, e.ExentoId, "sr-1");
        await FacturarAsync(sp, e, e.GravadoId, "sr-2");

        var libro = await sp.GetRequiredService<GetDocumentLedgerHandler>()
            .HandleAsync(e.OrgId, e.AuthId);

        libro!.Cai.Should().Be("CAI-QA");
        libro.Documents.Should().HaveCount(2);
        libro.Documents.Select(d => d.Sequence).Should().BeEquivalentTo([1L, 2L]);
        libro.Gaps.Should().BeEmpty("no se perdió ningún correlativo");
    }

    [Fact]
    public async Task UnCorrelativoPerdido_apareceComoHuecoConSuExplicacion()
    {
        // 🔴 Una auditoría pregunta por los huecos. No basta con no tenerlos: hay que poder
        // explicarlos. Acá se fuerza uno reservando un número sin emitir.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await FacturarAsync(sp, e, e.ExentoId, "sr-1"); // consume el 1

        // Reserva sin emitir: el 2 se pierde.
        var reserva = await sp.GetRequiredService<IFiscalRepository>()
            .ReserveNextSequenceAsync(e.OrgId, e.AuthId, new DateOnly(2026, 8, 25));
        reserva.Sequence.Should().Be(2);

        await FacturarAsync(sp, e, e.GravadoId, "sr-2"); // consume el 3

        var libro = await sp.GetRequiredService<GetDocumentLedgerHandler>()
            .HandleAsync(e.OrgId, e.AuthId);

        libro!.Gaps.Should().ContainSingle();
        libro.Gaps[0].From.Should().Be(2);
        libro.Gaps[0].To.Should().Be(2);
        libro.Gaps[0].Count.Should().Be(1);
        libro.Gaps[0].Explanation.Should().ContainEquivalentOf("no se reutiliza");
    }

    [Theory]
    // Sin huecos.
    [InlineData(new long[] { 1, 2, 3 }, 1L, 3L, 0)]
    // Un hueco en el medio.
    [InlineData(new long[] { 1, 3 }, 1L, 3L, 1)]
    // Un hueco al principio: la reserva falló antes del primer documento.
    [InlineData(new long[] { 2, 3 }, 1L, 3L, 1)]
    // Un hueco al final: se reservó y no se emitió.
    [InlineData(new long[] { 1, 2 }, 1L, 3L, 1)]
    // Dos huecos separados.
    [InlineData(new long[] { 1, 3, 5 }, 1L, 5L, 2)]
    // Nada emitido: un solo hueco que cubre todo lo entregado.
    [InlineData(new long[] { }, 1L, 3L, 1)]
    public void LosHuecos_seDetectanEnTodosLosBordes(
        long[] emitidos, long desde, long ultimo, int huecosEsperados)
    {
        // Es aritmética de rangos —fácil de equivocar en los bordes— y el resultado va a una
        // auditoría. Se prueba la función sola.
        var gaps = GetDocumentLedgerHandler.DetectGaps(emitidos, desde, ultimo);

        gaps.Should().HaveCount(huecosEsperados);
    }

    [Fact]
    public void UnRangoContiguoDeHuecos_seReportaComoUnoSolo()
    {
        // Listar treinta y cuatro filas idénticas esconde el dato en vez de mostrarlo.
        var gaps = GetDocumentLedgerHandler.DetectGaps([1, 40], 1, 40);

        gaps.Should().ContainSingle();
        gaps[0].From.Should().Be(2);
        gaps[0].To.Should().Be(39);
        gaps[0].Count.Should().Be(38);
    }

    [Fact]
    public void LoQueTodaviaNoSeEntrego_noEsHueco()
    {
        // El rango disponible no es un hueco: es rango disponible.
        var gaps = GetDocumentLedgerHandler.DetectGaps([1, 2], rangeFrom: 1, lastDelivered: 2);

        gaps.Should().BeEmpty();
    }

    // ── Corte de caja ───────────────────────────────────────────────────────

    [Fact]
    public async Task ElCorteDelDia_separaPorMetodoYMuestraElArqueo()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();

        var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, cajero, 1000m);

        var f1 = await FacturarAsync(sp, e, e.ExentoId, "sr-1");   // 500 exento
        var f2 = await FacturarAsync(sp, e, e.GravadoId, "sr-2");  // 4600 con ISV

        var cobrar = sp.GetRequiredService<ReceivePaymentHandler>();
        await cobrar.HandleAsync(new ReceivePaymentCommand(
            e.OrgId, turno.Session!.Id, e.PartyId, cajero, [f1],
            [new TenderLine("Cash", 500m, null)]));
        await cobrar.HandleAsync(new ReceivePaymentCommand(
            e.OrgId, turno.Session.Id, e.PartyId, cajero, [f2],
            [new TenderLine("Card", 4600m, "VOUCHER-1")]));

        await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(e.OrgId, turno.Session.Id, cajero, 1500m, null, null));

        var corte = await sp.GetRequiredService<GetDailyCashCutHandler>()
            .HandleAsync(e.OrgId, null, null);

        corte.Shifts.Should().ContainSingle();
        var t = corte.Shifts[0];
        t.CashPointName.Should().Be("Caja 1");
        t.Status.Should().Be("Closed");
        t.ExpectedAmount.Should().Be(1500m, "solo el efectivo toca la gaveta");
        t.Difference.Should().Be(0m);

        corte.TotalsByMethod.Should().HaveCount(2);
        corte.TotalsByMethod.Single(m => m.Method == "Cash").Amount.Should().Be(500m);
        corte.TotalsByMethod.Single(m => m.Method == "Card").Amount.Should().Be(4600m);
        corte.TotalCollected.Should().Be(5100m);
        corte.OpenShifts.Should().Be(0);
    }

    [Fact]
    public async Task ElCorte_muestraLosTurnosAbiertosSinArqueo()
    {
        // Un corte que solo muestra lo cerrado le esconde al dueño la caja que quedó abierta desde
        // ayer, que es justamente lo que quiere ver.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, Guid.NewGuid(), 1000m);

        var corte = await sp.GetRequiredService<GetDailyCashCutHandler>()
            .HandleAsync(e.OrgId, null, null);

        corte.OpenShifts.Should().Be(1);
        corte.Shifts[0].Status.Should().Be("Open");
        corte.Shifts[0].ExpectedAmount.Should().BeNull("el turno abierto no revela el esperado");
        corte.TotalDifference.Should().Be(0m);
    }

    [Fact]
    public async Task ElCorte_incluyeLosRetiros()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();

        var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, cajero, 1000m);
        await sp.GetRequiredService<RegisterCashMovementHandler>().HandleAsync(
            new RegisterMovementCommand(
                e.OrgId, turno.Session!.Id, "Withdrawal", 600m, "Retiro a bóveda", cajero, null));

        var corte = await sp.GetRequiredService<GetDailyCashCutHandler>()
            .HandleAsync(e.OrgId, null, null);

        corte.Shifts[0].Withdrawals.Should().Be(600m);
    }

    // ── Reporte de excepciones ──────────────────────────────────────────────

    [Fact]
    public async Task ElReporteDeExcepciones_juntaLasCuatroClases()
    {
        // La pregunta del dueño es una sola: "¿qué pasó fuera de lo normal y quién lo autorizó?".
        // Repartirlo en cuatro pantallas obliga a cruzarlas a mano.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        var supervisor = Guid.NewGuid();

        // 1. Cargo manual.
        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ExentoId, ChargeSourceKind.Manual, "m-1",
                Reason: "Cargado a mano por QA"));

        // 2. Descuento autorizado.
        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.GravadoId, ChargeSourceKind.Manual, "m-2",
                DiscountAmount: 500m, DiscountAuthorizedBy: supervisor,
                Reason: "Descuento autorizado"));

        // 3. Servicio liberado.
        await sp.GetRequiredService<WaiveServiceHandler>().HandleAsync(
            new WaiveServiceCommand(
                e.OrgId, "ServiceRequest/sr-9", supervisor, "Caso social", null));

        // 4. Arqueo con diferencia.
        var turno = await sp.GetRequiredService<OpenCashSessionHandler>()
            .HandleAsync(e.OrgId, e.CashPointId, cajero, 1000m);
        await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(
                e.OrgId, turno.Session!.Id, cajero, 950m, "Vuelto mal dado", null));

        var r = await sp.GetRequiredService<GetExceptionReportHandler>()
            .HandleAsync(e.OrgId, null, null);

        r.ManualCharges.Should().Be(2, "los dos cargos entraron a mano");
        r.AuthorizedDiscounts.Should().Be(1);
        r.WaivedServices.Should().Be(1);
        r.CashDifferences.Should().Be(1);

        r.Rows.Should().Contain(x => x.Kind == "WaivedService" && x.AuthorizedBy == supervisor);
        r.Rows.Should().Contain(x => x.Kind == "CashDifference" && x.Amount == -50m);
        r.Rows.Should().Contain(x => x.Kind == "AuthorizedDiscount" && x.AuthorizedBy == supervisor);
    }

    [Fact]
    public async Task LosReportesDeOtraOrganizacion_vienenVacios()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await FacturarAsync(sp, e, e.ExentoId, "sr-1");

        var otra = Guid.NewGuid();

        (await sp.GetRequiredService<GetTaxReportHandler>().HandleAsync(otra, null, null))
            .DocumentCount.Should().Be(0);
        (await sp.GetRequiredService<GetDailyCashCutHandler>().HandleAsync(otra, null, null))
            .Shifts.Should().BeEmpty();
        (await sp.GetRequiredService<GetDocumentLedgerHandler>().HandleAsync(otra, e.AuthId))
            .Should().BeNull();
    }
}
