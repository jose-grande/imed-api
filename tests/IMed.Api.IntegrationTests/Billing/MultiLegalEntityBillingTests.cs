using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Organizations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Una cadena con dos personas jurídicas (FAC-9, FAC-9b): Central (razón social A) y Norte
/// (razón social B), cada una con su CAI y su caja, y un anexo de Central que comparte el RTN A.
///
/// <para>Las reglas que se prueban son las que impiden mezclar contribuyentes: un documento fiscal
/// lleva cargos de una sola razón social, la caja cobra solo lo de su razón social, quien tiene un
/// turno abierto factura con la sede de su caja, y el ISV se reporta por razón social.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class MultiLegalEntityBillingTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string RtnA = "08019999123456";
    private const string RtnB = "08011999654321";

    private sealed record Cadena(
        Guid OrgId, Guid AccountId, Guid PartyId, Guid FacturaTypeId, Guid ItemId,
        Guid Central, Guid Anexo, Guid Norte,
        Guid RazonA, Guid RazonB,
        Guid CajaCentral, Guid CajaAnexo, Guid CajaNorte);

    private async Task<Cadena> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var central = TestSedes.Create(db, orgId, "CENTRAL", "Sede Central", isDefault: true);
        central.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(central);
        var razonA = db.LegalEntities.Local.Single(r => r.OrganizationId == orgId);

        var anexo = OrganizationLocation.Create(orgId, "ANEXO", "Anexo Central", razonA.Id);
        anexo.SetFiscalEstablishmentCode("003");
        db.OrganizationLocations.Add(anexo);

        var razonB = LegalEntity.Create(orgId, "Clínica del Norte S.A.", RtnB, "Col. Kennedy");
        db.LegalEntities.Add(razonB);
        var norte = OrganizationLocation.Create(orgId, "NORTE", "Sede Norte", razonB.Id);
        norte.SetFiscalEstablishmentCode("002");
        db.OrganizationLocations.Add(norte);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", RtnA, "Clínica de Prueba S. de R.L.");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;

        var cai = sp.GetRequiredService<RegisterFiscalAuthorizationHandler>();
        foreach (var (sede, codigo) in new[] { (central.Id, "CAI-A"), (anexo.Id, "CAI-A"), (norte.Id, "CAI-B") })
        {
            await cai.HandleAsync(new RegisterFiscalAuthorizationCommand(
                orgId, sede, factura, codigo, "001", 1, 500, new DateOnly(2027, 12, 31)));
        }

        var cajas = sp.GetRequiredService<CreateCashPointHandler>();
        var cajaCentral = await cajas.HandleAsync(new CreateCashPointCommand(orgId, central.Id, "CAJA-C", "Caja Central", "001", "Services", false, null));
        var cajaAnexo = await cajas.HandleAsync(new CreateCashPointCommand(orgId, anexo.Id, "CAJA-A", "Caja Anexo", "001", "Services", false, null));
        var cajaNorte = await cajas.HandleAsync(new CreateCashPointCommand(orgId, norte.Id, "CAJA-N", "Caja Norte", "001", "Services", false, null));

        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(orgId, "CONS_GEN", "Consulta general", "Service", exento, 500m,
                "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-cadena", "Ana López", $"enc-{Guid.NewGuid():N}", "Episode"));
        var parte = await sp.GetRequiredService<IPartyRepository>().FindByPatientAsync(orgId, "pat-cadena");

        return new Cadena(orgId, cuenta.Id, parte!.Id, factura, item.Id,
            central.Id, anexo.Id, norte.Id, razonA.Id, razonB.Id,
            cajaCentral.Id, cajaAnexo.Id, cajaNorte.Id);
    }

    private static async Task CobrarEnAsync(IServiceProvider sp, Cadena c, Guid sede, string origen)
    {
        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(new CaptureChargeCommand(
            c.OrgId, c.AccountId, c.ItemId, ChargeSourceKind.LabOrder, origen, LocationId: sede));
        r.Succeeded.Should().BeTrue(r.Rejected);
    }

    private static Task<IssueInvoiceResult> EmitirEnAsync(IServiceProvider sp, Cadena c, Guid sede, Guid? usuario = null) =>
        sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(c.OrgId, c.AccountId, sede, c.FacturaTypeId, UserId: usuario));

    private static async Task<Guid> AbrirTurnoAsync(IServiceProvider sp, Cadena c, Guid caja, Guid cajero)
    {
        var r = await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(c.OrgId, caja, cajero, 1000m);
        r.Succeeded.Should().BeTrue(r.Message);
        return r.Session!.Id;
    }

    [Fact]
    public async Task UnaFactura_llevaSoloLosCargosDeSuRazonSocial_yAvisaLosDemas()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Central, "sr-central");
        await CobrarEnAsync(sp, c, c.Norte, "sr-norte");

        var enCentral = await EmitirEnAsync(sp, c, c.Central);

        enCentral.Succeeded.Should().BeTrue(enCentral.Rejected?.Message);
        enCentral.Invoice!.Lines.Should().ContainSingle("el cargo de Norte es de otra persona jurídica");
        enCentral.Invoice.LegalEntityId.Should().Be(c.RazonA);
        enCentral.Invoice.PendingOtherIssuers.Should().ContainSingle()
            .Which.Should().Match<PendingIssuerDto>(p => p.LegalEntityId == c.RazonB && p.ChargeCount == 1);

        var enNorte = await EmitirEnAsync(sp, c, c.Norte);
        enNorte.Succeeded.Should().BeTrue(enNorte.Rejected?.Message);
        enNorte.Invoice!.FiscalDocument!.FullNumber.Should().StartWith("002-");
        enNorte.Invoice.PendingOtherIssuers.Should().BeEmpty();
    }

    [Fact]
    public async Task SiTodosLosCargosSonDeOtraRazonSocial_noSeEmite()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Norte, "sr-norte");

        var r = await EmitirEnAsync(sp, c, c.Central);

        r.Succeeded.Should().BeFalse();
        r.Rejected!.Code.Should().Be("cargos_de_otro_emisor");
        r.Rejected.Message.Should().Contain("Clínica del Norte");
    }

    [Fact]
    public async Task UnaSucursalDeLaMismaRazonSocial_facturaLosCargosDeLaOtra()
    {
        // Mismo RTN, otro establecimiento: es el mismo contribuyente.
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Central, "sr-central");

        var r = await EmitirEnAsync(sp, c, c.Anexo);

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.Invoice!.FiscalDocument!.FullNumber.Should().StartWith("003-");
    }

    [Fact]
    public async Task ConTurnoAbiertoEnOtraSede_noSeEmiteDesdeEsta()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var cajero = Guid.NewGuid();
        await AbrirTurnoAsync(sp, c, c.CajaNorte, cajero);
        await CobrarEnAsync(sp, c, c.Central, "sr-central");

        var r = await EmitirEnAsync(sp, c, c.Central, cajero);

        r.Succeeded.Should().BeFalse("la factura y el dinero tienen que quedar en la misma caja");
        r.Rejected!.Code.Should().Be("sede_distinta_caja");
    }

    [Fact]
    public async Task LaCajaDeOtraRazonSocial_noCobraLaFactura()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Central, "sr-central");
        var factura = (await EmitirEnAsync(sp, c, c.Central)).Invoice!;
        var cajero = Guid.NewGuid();
        var turno = await AbrirTurnoAsync(sp, c, c.CajaNorte, cajero);

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(new ReceivePaymentCommand(
            c.OrgId, turno, c.PartyId, cajero, [factura.Id], [new TenderLine("Cash", factura.GrandTotal, null)]));

        r.Code.Should().Be("factura_de_otro_emisor",
            "el dinero entraría al arqueo de Norte por una factura de Central");
    }

    [Fact]
    public async Task LaCajaDeOtraSucursalDeLaMismaRazonSocial_siCobra()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Central, "sr-central");
        var factura = (await EmitirEnAsync(sp, c, c.Central)).Invoice!;
        var cajero = Guid.NewGuid();
        var turno = await AbrirTurnoAsync(sp, c, c.CajaAnexo, cajero);

        var r = await sp.GetRequiredService<ReceivePaymentHandler>().HandleAsync(new ReceivePaymentCommand(
            c.OrgId, turno, c.PartyId, cajero, [factura.Id], [new TenderLine("Cash", factura.GrandTotal, null)]));

        r.Code.Should().BeNull(r.Message);
    }

    [Fact]
    public async Task ElIsv_seReportaPorRazonSocial()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarEnAsync(sp, c, c.Central, "sr-central");
        await CobrarEnAsync(sp, c, c.Norte, "sr-norte");
        await EmitirEnAsync(sp, c, c.Central);
        await EmitirEnAsync(sp, c, c.Norte);
        var reporte = sp.GetRequiredService<GetTaxReportHandler>();

        var deA = await reporte.HandleAsync(c.OrgId, null, null, c.RazonA);
        var deB = await reporte.HandleAsync(c.OrgId, null, null, c.RazonB);

        deA.DocumentCount.Should().Be(1);
        deA.TaxId.Should().Be(RtnA);
        deB.DocumentCount.Should().Be(1);
        deB.TaxId.Should().Be(RtnB);

        var sinElegir = async () => await reporte.HandleAsync(c.OrgId, null, null);
        await sinElegir.Should().ThrowAsync<ArgumentException>("sumar dos RTN no le sirve a ninguno");
    }

    [Fact]
    public async Task ElCorteDeCaja_esPorRazonSocial()
    {
        var c = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await AbrirTurnoAsync(sp, c, c.CajaCentral, Guid.NewGuid());
        await AbrirTurnoAsync(sp, c, c.CajaNorte, Guid.NewGuid());

        var corte = await sp.GetRequiredService<GetDailyCashCutHandler>()
            .HandleAsync(c.OrgId, null, null, c.RazonB);

        corte.Shifts.Should().ContainSingle().Which.CashPointId.Should().Be(c.CajaNorte);
    }

    [Fact]
    public async Task NoSeEmiteEnUnaSedeDondeElUsuarioNoTrabaja()
    {
        // D4: la sede de emisión ya no se acepta a ciegas del cuerpo del request.
        var c = await ArrangeAsync();
        await Task.Delay(500);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: c.OrgId.ToString(), realmRoles: ["cashier"]));

        var resp = await client.PostAsJsonAsync("/api/billing/invoices", new
        {
            accountId = c.AccountId,
            locationId = c.Norte,
            documentTypeId = c.FacturaTypeId,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "el usuario quedó en la sede principal: no puede emitir con el CAI de Norte");
    }
}
