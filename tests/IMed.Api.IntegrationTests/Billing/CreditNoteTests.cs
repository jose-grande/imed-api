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
/// Nota de crédito (D10, A9): total o por líneas, con su número fiscal, su efecto en el saldo, la
/// devolución desde caja y el ISV neto.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CreditNoteTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid LocationId, Guid FacturaTypeId, Guid CashPointId,
        Guid ConsultaId, Guid GravadoId);

    private async Task<Escenario> ArrangeAsync(bool conCaiDeNota = true)
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
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        var nota = setup.DocumentTypes.First(t => t.Code == "nota_credito").Id;
        var registrar = sp.GetRequiredService<RegisterFiscalAuthorizationHandler>();
        await registrar.HandleAsync(new RegisterFiscalAuthorizationCommand(
            orgId, sede.Id, factura, "CAI-FAC", "002", 1, 500, new DateOnly(2027, 12, 31)));
        if (conCaiDeNota)
        {
            await registrar.HandleAsync(new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, nota, "CAI-NC", "002", 1, 100, new DateOnly(2027, 12, 31)));
        }
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));

        var items = sp.GetRequiredService<CreateBillableItemHandler>();
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;
        var consulta = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "CONS", "Consulta", "Service", exento, 500m, null, null, null, null, null));
        var gravado = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "EST", "Procedimiento estético", "Service", isv15, 1000m, null, null, null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-nc", "Ana López", $"enc-{Guid.NewGuid():N}", "Episode"));
        return new Escenario(orgId, cuenta.Id, sede.Id, factura, caja.Id, consulta.Id, gravado.Id);
    }

    private async Task<InvoiceDto> FacturarAsync(Escenario e)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var captura = sp.GetRequiredService<CaptureChargeHandler>();
        (await captura.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ConsultaId, ChargeSourceKind.LabOrder, "s1", LocationId: e.LocationId))).Succeeded.Should().BeTrue();
        (await captura.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.GravadoId, ChargeSourceKind.LabOrder, "s2", 2m, LocationId: e.LocationId))).Succeeded.Should().BeTrue();
        var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId, BuyerName: "Ana López"));
        f.Succeeded.Should().BeTrue(f.Rejected?.Message);
        // 500 exento + 2 × 1000 + 15 % = 500 + 2000 + 300 = 2800.
        f.Invoice!.GrandTotal.Should().Be(2800m);
        return f.Invoice;
    }

    private async Task<IssueCreditNoteResult> AcreditarAsync(
        Escenario e, Guid invoiceId, IReadOnlyList<CreditNoteLineRequest>? lineas = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IssueCreditNoteHandler>().HandleAsync(
            new IssueCreditNoteCommand(e.OrgId, invoiceId, "Devolución por error de cobro", Guid.NewGuid(), lineas));
    }

    [Fact]
    public async Task NotaTotal_sobreFacturaSinCobrar_laDejaAcreditada_yElIsvNetoEsCero()
    {
        var e = await ArrangeAsync();
        var f = await FacturarAsync(e);

        var r = await AcreditarAsync(e, f.Id);

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.CreditNote!.GrandTotal.Should().Be(2800m);
        r.CreditNote.FullNumber.Should().Be("001-002-03-00000001", "tipo 03 = nota de crédito, su propio correlativo");
        r.CreditNote.AppliedToBalance.Should().Be(2800m);
        r.CreditNote.RefundDue.Should().Be(0m, "no se había cobrado nada");

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var factura = await sp.GetRequiredService<GetInvoiceHandler>().HandleAsync(e.OrgId, f.Id);
        factura!.Status.Should().Be("Credited");
        factura.Balance.Should().Be(0m);
        factura.CreditedTotal.Should().Be(2800m);

        var isv = await sp.GetRequiredService<GetTaxReportHandler>().HandleAsync(e.OrgId, null, null);
        isv.CreditNoteCount.Should().Be(1);
        isv.DocumentCount.Should().Be(1);
        isv.GrandTotal.Should().Be(0m, "la nota resta lo que la factura sumó");
        isv.TaxTotal.Should().Be(0m);

        (await AcreditarAsync(e, f.Id)).Rejected!.Code.Should().Be("estado_invalido", "ya no queda nada por acreditar");

        // Se imprime con el mismo PDF que la factura, con su número de nota.
        var pdf = await sp.GetRequiredService<GetInvoicePdfHandler>().HandleAsync(e.OrgId, r.CreditNote.Id);
        pdf.Should().NotBeNull();
        pdf!.FileName.Should().Be("001-002-03-00000001.pdf");
        pdf.Bytes.Length.Should().BeGreaterThan(1000);
    }

    [Fact]
    public async Task NotaPorLinea_sobreFacturaCobrada_dejaDevolucion_queSePagaDesdeLaCaja()
    {
        var e = await ArrangeAsync();
        var f = await FacturarAsync(e);
        var cajero = Guid.NewGuid();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var turno = await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(e.OrgId, e.CashPointId, cajero, 0m);
            var parte = await sp.GetRequiredService<IPartyRepository>().FindByPatientAsync(e.OrgId, "pat-nc");
            await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(new ReceivePaymentCommand(
                e.OrgId, turno.Session!.Id, parte!.Id, cajero, [f.Id], [new TenderLine("Cash", 2800m, null)]));
            (await sp.GetRequiredService<GetInvoiceHandler>().HandleAsync(e.OrgId, f.Id))!.Status.Should().Be("Paid");
        }

        // Una de las dos unidades del procedimiento: 1000 + 15 % = 1150.
        var linea = f.Lines.Single(l => l.Quantity == 2m);
        var r = await AcreditarAsync(e, f.Id, [new CreditNoteLineRequest(linea.Id!.Value, 1m)]);

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.CreditNote!.GrandTotal.Should().Be(1150m);
        r.CreditNote.AppliedToBalance.Should().Be(0m, "la factura ya estaba pagada");
        r.CreditNote.RefundDue.Should().Be(1150m);

        // No se puede acreditar más de lo que queda de la línea.
        (await AcreditarAsync(e, f.Id, [new CreditNoteLineRequest(linea.Id!.Value, 2m)]))
            .Rejected!.Code.Should().Be("lineas_invalidas");

        using var scope2 = fixture.Factory.Services.CreateScope();
        var sp2 = scope2.ServiceProvider;
        var devolver = sp2.GetRequiredService<RefundCreditNoteHandler>();
        var (nota, error) = await devolver.HandleAsync(e.OrgId, r.CreditNote.Id, cajero);
        error.Should().BeNull();
        nota!.RefundedAt.Should().NotBeNull();

        var (_, otraVez) = await devolver.HandleAsync(e.OrgId, r.CreditNote.Id, cajero);
        otraVez.Should().Contain("ya se registró");

        var turnoAbierto = await sp2.GetRequiredService<ICashRepository>().FindOpenSessionByCashierAsync(e.OrgId, cajero);
        var movimientos = await sp2.GetRequiredService<ICashRepository>().ListMovementsAsync(e.OrgId, turnoAbierto!.Id);
        movimientos.Should().ContainSingle(m => m.Amount == 1150m && m.Reason.Contains("001-002-03"));
    }

    [Fact]
    public async Task SinCaiDeNotas_noSeEmite_yLaFacturaNoCambia()
    {
        var e = await ArrangeAsync(conCaiDeNota: false);
        var f = await FacturarAsync(e);

        var r = await AcreditarAsync(e, f.Id);

        r.Rejected!.Code.Should().Be("sin_autorizacion");
        using var scope = fixture.Factory.Services.CreateScope();
        var factura = await scope.ServiceProvider.GetRequiredService<GetInvoiceHandler>().HandleAsync(e.OrgId, f.Id);
        factura!.Balance.Should().Be(2800m);
        factura.CreditedTotal.Should().Be(0m);
    }
}
