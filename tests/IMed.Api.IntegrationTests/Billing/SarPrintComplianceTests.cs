using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Invoicing;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Lo que exige el Reglamento del Régimen de Facturación (Acuerdo 481-2017 y su reforma 725-2018)
/// del documento emitido. Ver <c>imed-infra/docs/qa/Auditoria-Impresion-Factura-SAR.md</c>.
/// <list type="bullet">
///   <item>Códigos de tipo del SAR en el número: 01 factura, 07 nota de crédito, 08 nota de débito.</item>
///   <item>El rango autorizado completo, como lo emite el SAR.</item>
///   <item>Adquirente exonerado: lo gravado sale exonerado, sin ISV, con sus números congelados.</item>
/// </list>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class SarPrintComplianceTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid AccountId, Guid LocationId, Guid FacturaTypeId, Guid ConsultaId, Guid GravadoId);

    private async Task<(Escenario E, FiscalSetupDto Setup)> ArrangeAsync()
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
        await registrar.HandleAsync(new RegisterFiscalAuthorizationCommand(
            orgId, sede.Id, nota, "CAI-NC", "002", 1, 100, new DateOnly(2027, 12, 31)));

        var items = sp.GetRequiredService<CreateBillableItemHandler>();
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;
        var consulta = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "CONS", "Consulta", "Service", exento, 500m, null, null, null, null, null));
        var gravado = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "EST", "Procedimiento estético", "Service", isv15, 1000m, null, null, null, null, null));
        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-sar", "Ana López", $"enc-{Guid.NewGuid():N}", "Episode"));
        return (new Escenario(orgId, cuenta.Id, sede.Id, factura, consulta.Id, gravado.Id), setup);
    }

    private async Task<IssueInvoiceResult> FacturarAsync(Escenario e, BuyerExemption? exoneracion = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var captura = sp.GetRequiredService<CaptureChargeHandler>();
        (await captura.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.ConsultaId, ChargeSourceKind.LabOrder, "s1", LocationId: e.LocationId))).Succeeded.Should().BeTrue();
        (await captura.HandleAsync(new CaptureChargeCommand(
            e.OrgId, e.AccountId, e.GravadoId, ChargeSourceKind.LabOrder, "s2", 2m, LocationId: e.LocationId))).Succeeded.Should().BeTrue();
        return await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId,
                BuyerName: "Fundación de Prueba", BuyerTaxId: "08019999000001", Exemption: exoneracion));
    }

    private async Task<FiscalDocument> DocumentoAsync(Guid orgId, Guid invoiceId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetFiscalDocumentAsync(orgId, invoiceId))!;
    }

    [Fact]
    public async Task LosTiposDeDocumento_usanLosCodigosDelSar_ySinProforma()
    {
        var (_, setup) = await ArrangeAsync();

        setup.DocumentTypes.Select(t => (t.Code, t.DocumentCode)).Should().BeEquivalentTo(new[]
        {
            ("factura", "01"), ("nota_credito", "07"), ("nota_debito", "08"),
        }, "art. 10.7, 25 y 27; la proforma no es documento fiscal y su 04 es el Recibo por Honorarios");
    }

    [Fact]
    public async Task ElRangoAutorizado_seCongelaCompleto_enLaFacturaYEnLaNota()
    {
        var (e, _) = await ArrangeAsync();
        var f = await FacturarAsync(e);
        f.Succeeded.Should().BeTrue(f.Rejected?.Message);

        var doc = await DocumentoAsync(e.OrgId, f.Invoice!.Id);
        doc.FullNumber.Should().Be("001-002-01-00000001");
        doc.RangeSnapshot.Should().Be("001-002-01-00000001 al 001-002-01-00000500", "art. 10.5: como lo emite el SAR");

        using var scope = fixture.Factory.Services.CreateScope();
        var nc = await scope.ServiceProvider.GetRequiredService<IssueCreditNoteHandler>().HandleAsync(
            new IssueCreditNoteCommand(e.OrgId, f.Invoice.Id, "Error de cobro", Guid.NewGuid(), null));
        nc.Succeeded.Should().BeTrue(nc.Rejected?.Message);
        nc.CreditNote!.FullNumber.Should().Be("001-002-07-00000001");
        (await DocumentoAsync(e.OrgId, nc.CreditNote.Id)).RangeSnapshot
            .Should().Be("001-002-07-00000001 al 001-002-07-00000100");
    }

    [Fact]
    public async Task ElAdquirenteExonerado_noPagaIsv_yLaFacturaLlevaSusNumeros()
    {
        var (e, _) = await ArrangeAsync();

        var f = await FacturarAsync(e, new BuyerExemption(" OCE-2026-15 ", null, "  "));

        f.Succeeded.Should().BeTrue(f.Rejected?.Message);
        // 500 exento + 2 × 1000 gravado que pasa a exonerado: sin los 300 de ISV.
        f.Invoice!.GrandTotal.Should().Be(2500m);
        var doc = await DocumentoAsync(e.OrgId, f.Invoice.Id);
        doc.SubtotalExempt.Should().Be(500m, "lo exento sigue exento");
        doc.SubtotalExonerated.Should().Be(2000m);
        doc.SubtotalTaxed15.Should().Be(0m);
        doc.TaxTotal.Should().Be(0m);
        doc.BuyerExemptPurchaseOrder.Should().Be("OCE-2026-15");
        doc.BuyerExemptRegistry.Should().BeNull();
        doc.BuyerSagRegistry.Should().BeNull();

        // La nota de crédito hereda la exoneración: revierte exactamente lo mismo.
        using var scope = fixture.Factory.Services.CreateScope();
        var nc = await scope.ServiceProvider.GetRequiredService<IssueCreditNoteHandler>().HandleAsync(
            new IssueCreditNoteCommand(e.OrgId, f.Invoice.Id, "Devolución", Guid.NewGuid(), null));
        nc.Succeeded.Should().BeTrue(nc.Rejected?.Message);
        (await DocumentoAsync(e.OrgId, nc.CreditNote!.Id)).BuyerExemptPurchaseOrder.Should().Be("OCE-2026-15");
    }

    [Fact]
    public async Task SinNumerosDeExoneracion_seFacturaComoSiempre()
    {
        var (e, _) = await ArrangeAsync();

        var f = await FacturarAsync(e, new BuyerExemption("", " ", null));

        f.Invoice!.GrandTotal.Should().Be(2800m, "con 300 de ISV del 15 %");
        (await DocumentoAsync(e.OrgId, f.Invoice.Id)).BuyerExemptPurchaseOrder.Should().BeNull();
    }

    [Fact]
    public async Task LosPdf_seGeneranEnCartaYTicket_paraFacturaExoneradaYNota()
    {
        var (e, _) = await ArrangeAsync();
        var f = await FacturarAsync(e, new BuyerExemption("OCE-2026-15", "CE-778", "SAG-91"));
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var nc = await sp.GetRequiredService<IssueCreditNoteHandler>().HandleAsync(
            new IssueCreditNoteCommand(e.OrgId, f.Invoice!.Id, "Devolución", Guid.NewGuid(), null));
        var pdf = sp.GetRequiredService<GetInvoicePdfHandler>();

        // Con IMED_PDF_DUMP se guardan para revisarlos a mano (pdftotext): el texto del PDF va
        // codificado por glifo y no se puede afirmar desde la prueba.
        var dump = Environment.GetEnvironmentVariable("IMED_PDF_DUMP");
        foreach (var (id, nombre) in new[] { (f.Invoice.Id, "factura"), (nc.CreditNote!.Id, "nota") })
        {
            foreach (var formato in new[] { InvoicePrintFormat.Letter, InvoicePrintFormat.Thermal80 })
            {
                var r = await pdf.HandleAsync(e.OrgId, id, formato);
                r.Should().NotBeNull();
                r!.Bytes.Should().NotBeEmpty();
                if (!string.IsNullOrWhiteSpace(dump))
                {
                    Directory.CreateDirectory(dump);
                    await File.WriteAllBytesAsync(Path.Combine(dump, $"{nombre}-{formato}.pdf"), r.Bytes);
                }
            }
        }

        var db = sp.GetRequiredService<AppDbContext>();
        (await db.FiscalDocuments.AsNoTracking().CountAsync(d => d.OrganizationId == e.OrgId)).Should().Be(2);
    }

    [Fact]
    public async Task ElCobro_avisaQueFaltanDatosDelEmisor_ydejaDeAvisarAlCompletarlos()
    {
        var (e, _) = await ArrangeAsync();
        var usuario = Guid.NewGuid();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<CaptureChargeHandler>().HandleAsync(new CaptureChargeCommand(
                e.OrgId, e.AccountId, e.ConsultaId, ChargeSourceKind.LabOrder, "s1", LocationId: e.LocationId)))
                .Succeeded.Should().BeTrue();
        }

        async Task<string?> Aviso()
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<AccountCheckoutHandler>()
                .GetAsync(e.OrgId, usuario, e.AccountId))!.IssuerWarning;
        }

        var antes = await Aviso();
        antes.Should().Contain("dirección").And.Contain("teléfono").And.Contain("correo", "art. 10.1");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sede = await db.OrganizationLocations.SingleAsync(l => l.Id == e.LocationId);
            sede.UpdateContact("Col. Palmira, Tegucigalpa", "2200-0000");
            var razon = await db.LegalEntities.SingleAsync(r => r.Id == sede.LegalEntityId);
            razon.Update(razon.LegalName, razon.TaxId, razon.FiscalAddress, razon.TradeName, razon.Phone, "facturas@clinica.hn");
            await db.SaveChangesAsync();
        }

        (await Aviso()).Should().BeNull("con dirección, teléfono y correo el emisor está completo");
    }
}
