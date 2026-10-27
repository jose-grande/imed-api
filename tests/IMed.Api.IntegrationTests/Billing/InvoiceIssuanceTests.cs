using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Emisión y timbrado (FAC-5). Corre contra Postgres: lo que se prueba incluye la transacción
/// única y los índices que impiden timbrar dos veces la misma factura o repetir un número.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class InvoiceIssuanceTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(
        Guid OrgId, Guid AccountId, Guid LocationId, Guid FacturaTypeId,
        Guid ExentoItemId, Guid GravadoItemId);

    /// <summary>
    /// Organización lista para emitir: régimen con RTN, sede con código de establecimiento, CAI
    /// vigente, dos cobrables (uno exento y uno gravado) y una cuenta abierta.
    /// </summary>
    private async Task<Escenario> ArrangeAsync(
        string establecimiento = "001",
        long rangeTo = 500,
        DateOnly? limitDate = null,
        string? issuerTaxId = "08019999123456")
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        if (!string.IsNullOrEmpty(establecimiento))
        {
            sede.SetFiscalEstablishmentCode(establecimiento);
        }
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", issuerTaxId, "Clínica de Prueba S. de R.L.");

        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;

        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "A1B2C3-D4E5F6-A7B8C9", "002",
                1, rangeTo, limitDate ?? new DateOnly(2027, 12, 31)));

        var items = sp.GetRequiredService<CreateBillableItemHandler>();
        var consulta = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "CONS_GEN", "Consulta general", "Service", exento, 500m,
            "Prepaid", "Global", null, null, null));
        var estetica = await items.HandleAsync(new CreateBillableItemCommand(
            orgId, "EST_BOTOX", "Toxina botulínica", "Service", isv15, 4000m,
            "Prepaid", "Global", null, null, null));

        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(
                orgId, null, "pat-fac-1", "Juan Pérez", $"enc-{Guid.NewGuid():N}", "Episode"));

        return new Escenario(orgId, cuenta.Id, sede.Id, factura, consulta.Id, estetica.Id);
    }

    private static async Task CobrarAsync(
        IServiceProvider sp, Escenario e, Guid itemId, string sourceId, decimal cantidad = 1m, Guid? sede = null)
    {
        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(
                e.OrgId, e.AccountId, itemId, ChargeSourceKind.LabOrder, sourceId, cantidad, LocationId: sede));
        r.Succeeded.Should().BeTrue(r.Rejected);
    }

    [Fact]
    public async Task LaSedeSinCodigo_seDestrabaDesdeLaAplicacionYEmite()
    {
        // El arranque real de una clínica: la sede nace sin código de establecimiento porque lo
        // asigna el SAR. Hasta que alguien lo cargue, no sale ninguna factura.
        //
        // Todas las demás pruebas de facturación siembran el código llamando al dominio, así que
        // ninguna ejercía el camino por el que un usuario lo carga. Esta empieza donde empieza QA:
        // sin código, y usando solo lo que la aplicación expone.
        var e = await ArrangeAsync(establecimiento: "");
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-destrabe");

        var bloqueada = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        bloqueada.Succeeded.Should().BeFalse("sin establecimiento el número saldría incompleto");
        bloqueada.Rejected!.Code.Should().Be("sede_sin_codigo");

        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: e.OrgId.ToString(), realmRoles: ["billing_supervisor"]));

        var puesto = await client.PutAsJsonAsync(
            $"/api/locations/{e.LocationId}/fiscal-establishment", new { code = "001" });
        puesto.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        using var scope2 = fixture.Factory.Services.CreateScope();
        var r = await scope2.ServiceProvider.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        // El cargo siguió vivo esperando: cargar el código destraba lo que ya estaba, sin rehacerlo.
        r.Invoice!.FiscalDocument!.FullNumber.Should().Be("001-002-01-00000001");
    }

    [Fact]
    public async Task EmitirUnaFactura_produceElNumeroEnLosCuatroSegmentos()
    {
        // 🎯 Criterio de aceptación 1.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        var doc = r.Invoice!.FiscalDocument!;

        doc.EstablishmentCode.Should().Be("001");
        doc.EmissionPointCode.Should().Be("002");
        doc.DocumentTypeCode.Should().Be("01");
        doc.Sequence.Should().Be(1);
        doc.FullNumber.Should().Be("001-002-01-00000001");
        doc.AmountInWords.Should().Be("QUINIENTOS LEMPIRAS CON 00/100");
        doc.CaiSnapshot.Should().Be("A1B2C3-D4E5F6-A7B8C9");
        doc.RangeSnapshot.Should().Be("1-500");
        r.Invoice.Status.Should().Be("Issued");
    }

    [Fact]
    public async Task UnaFacturaConExentoYGravado_separaLosSubtotales()
    {
        // Criterio de aceptación 2. Es lo que el contador necesita para declarar: exento y cada
        // tasa gravada van en renglones distintos.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");
        await CobrarAsync(sp, e, e.GravadoItemId, "sr-2");

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var doc = r.Invoice!.FiscalDocument!;
        doc.SubtotalExempt.Should().Be(500m, "la consulta no paga ISV");
        doc.SubtotalTaxed15.Should().Be(4000m, "la estética sí");
        doc.SubtotalTaxed18.Should().Be(0m);
        doc.TaxTotal.Should().Be(600m);
        doc.GrandTotal.Should().Be(5100m);
    }

    [Fact]
    public async Task UnaVentaSobreDiezMilSinRtn_seBloquea()
    {
        // Criterio de aceptación 3. Regla del SAR, validada por el adaptador HN.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        // 3 × 4000 = 12 000 más ISV: pasa el umbral.
        await CobrarAsync(sp, e, e.GravadoItemId, "sr-1", cantidad: 3m);

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Succeeded.Should().BeFalse();
        r.Rejected!.Code.Should().Be("comprador_incompleto");
        r.Rejected.Message.Should().Contain("RTN");
    }

    [Fact]
    public async Task UnaVentaSobreDiezMilConRtn_seEmite()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.GravadoItemId, "sr-1", cantidad: 3m);

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(
                e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId,
                BuyerTaxId: "08011990123456", BuyerName: "Juan Pérez"));

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.Invoice!.FiscalDocument!.BuyerTaxId.Should().Be("08011990123456");
    }

    [Fact]
    public async Task UnaFacturaBloqueada_noConsumeCorrelativo()
    {
        // Cada intento fallido que consumiera un número dejaría un hueco que después hay que
        // explicar. Las validaciones van antes de la reserva a propósito.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.GravadoItemId, "sr-1", cantidad: 3m);

        // Falla por falta de RTN.
        await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        // Y ahora con RTN: tiene que llevarse el número 1, no el 2.
        var ok = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(
                e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId,
                BuyerTaxId: "08011990123456", BuyerName: "Juan Pérez"));

        ok.Invoice!.FiscalDocument!.Sequence.Should().Be(1);
    }

    [Fact]
    public async Task ReimprimirDevuelveElMismoDocumento_noOtroNuevo()
    {
        // Criterio de aceptación 4: leer la factura dos veces produce lo mismo. Si la impresión
        // falla, el documento ya existe y se reimprime — jamás se re-emite.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var emitida = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var leer = sp.GetRequiredService<GetInvoiceHandler>();
        var a = await leer.HandleAsync(e.OrgId, emitida.Invoice!.Id);
        var b = await leer.HandleAsync(e.OrgId, emitida.Invoice.Id);

        a!.FiscalDocument!.FullNumber.Should().Be(b!.FiscalDocument!.FullNumber);
        a.FiscalDocument.Sequence.Should().Be(b.FiscalDocument.Sequence);
        a.GrandTotal.Should().Be(b.GrandTotal);
        a.Lines.Should().BeEquivalentTo(b.Lines);
    }

    [Fact]
    public async Task LosCargosYaFacturados_noVuelvenAOtraFactura()
    {
        // Sin esto, emitir dos veces sobre la misma cuenta le cobra dos veces al paciente, con
        // dos documentos legales que lo respaldan.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var emitir = sp.GetRequiredService<IssueInvoiceHandler>();
        await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var segunda = await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        segunda.Succeeded.Should().BeFalse();
        segunda.Rejected!.Code.Should().Be("sin_cargos");
    }

    [Fact]
    public async Task ElPrecioDeLaLinea_esElDelCargoYNoElDelCatalogoDeHoy()
    {
        // La factura tiene que poder reimprimirse idéntica dentro de cinco años.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        // Sube el precio después de capturar el cargo.
        var lista = await sp.GetRequiredService<CreatePriceListHandler>().HandleAsync(
            new CreatePriceListCommand(
                e.OrgId, "PUB", "Pública", "Public", new DateOnly(2026, 1, 1), null, 0, "HNL"));
        await sp.GetRequiredService<SetPriceHandler>().HandleAsync(new SetPriceCommand(
            e.OrgId, lista.Id, e.ExentoItemId, 999m, new DateOnly(2026, 1, 1), null, null, null));

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Invoice!.Lines.Single().UnitPrice.Should().Be(500m);
        r.Invoice.GrandTotal.Should().Be(500m);
    }

    [Fact]
    public async Task AnularConsumeElCorrelativo_yLoDejaMarcado()
    {
        // Criterio de aceptación 5. Los huecos que deja una anulación son legítimos y explicables;
        // un número reutilizado no lo es.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var emitida = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));
        emitida.Invoice!.FiscalDocument!.Sequence.Should().Be(1);

        var anulada = await sp.GetRequiredService<VoidInvoiceHandler>().HandleAsync(
            e.OrgId, emitida.Invoice.Id, "Se emitió sobre la cuenta equivocada.");

        anulada.Succeeded.Should().BeTrue(anulada.Rejected?.Message);
        anulada.Invoice!.Status.Should().Be("Voided");
        anulada.Invoice.FiscalDocument!.Status.Should().Be("Voided");
        anulada.Invoice.FiscalDocument.VoidReason.Should().Contain("cuenta equivocada");

        // El número 1 quedó consumido: la próxima factura se lleva el 2.
        await CobrarAsync(sp, e, e.GravadoItemId, "sr-2");
        var siguiente = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        siguiente.Invoice!.FiscalDocument!.Sequence.Should().Be(2,
            "el correlativo de la anulada no vuelve al pozo");
    }

    [Fact]
    public async Task AnularSinMotivo_seRechaza()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");
        var emitida = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var r = await sp.GetRequiredService<VoidInvoiceHandler>()
            .HandleAsync(e.OrgId, emitida.Invoice!.Id, "  ");

        r.Succeeded.Should().BeFalse();
        r.Rejected!.Code.Should().Be("sin_motivo");
    }

    [Fact]
    public async Task SinCorrelativosDisponibles_elMensajeDiceQueHacer()
    {
        // Quedarse sin números un sábado con la clínica llena es un evento real. El mensaje tiene
        // que mandar al usuario a registrar un rango nuevo, no decir "error".
        var e = await ArrangeAsync(rangeTo: 1);
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var emitir = sp.GetRequiredService<IssueInvoiceHandler>();
        await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        await CobrarAsync(sp, e, e.GravadoItemId, "sr-2");
        var segunda = await emitir.HandleAsync(new IssueInvoiceCommand(
            e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        segunda.Succeeded.Should().BeFalse();
        segunda.Rejected!.Code.Should().Be("autorizacion_bloqueada");
        segunda.Rejected.Message.Should().Contain("rango nuevo");
    }

    [Fact]
    public async Task UnaSedeSinCodigoDeEstablecimiento_noEmite()
    {
        // El número saldría incompleto y la autoridad no lo reconocería.
        var e = await ArrangeAsync(establecimiento: "");
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Succeeded.Should().BeFalse();
        r.Rejected!.Code.Should().Be("sede_sin_codigo");
    }

    [Fact]
    public async Task UnRegimenSinRtnDelEmisor_noEmite()
    {
        // FSF-DEC-07: el RTN es de la organización y sale impreso. Sin él el documento no tiene
        // emisor reconocible.
        var e = await ArrangeAsync(issuerTaxId: null);
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");

        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        r.Succeeded.Should().BeFalse();
        r.Rejected!.Code.Should().Be("emisor_sin_rtn");
    }

    [Fact]
    public async Task LaFacturaDeOtraOrganizacion_noSeAlcanza()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-1");
        var emitida = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, e.LocationId, e.FacturaTypeId));

        var deOtra = await sp.GetRequiredService<GetInvoiceHandler>()
            .HandleAsync(Guid.NewGuid(), emitida.Invoice!.Id);

        deOtra.Should().BeNull();
    }

    [Fact]
    public async Task UnaSedeDeOtraRazonSocial_emiteConSuPropioRtn()
    {
        // FAC-9: una cadena con dos personas jurídicas. El RTN de la factura es el de la razón
        // social de la sede que emite, no el del régimen de la organización.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var razonNorte = IMed.Api.Domain.Organizations.LegalEntity.Create(
            e.OrgId, "Clínica del Norte S.A.", "08011999123456", "Col. Kennedy", tradeName: "Clínica del Norte");
        db.LegalEntities.Add(razonNorte);
        var norte = OrganizationLocation.Create(
            e.OrgId, "NORTE", "Sede Norte", razonNorte.Id, address: "Bulevar Kennedy");
        norte.SetFiscalEstablishmentCode("002");
        db.OrganizationLocations.Add(norte);
        await db.SaveChangesAsync();

        // Mismo CAI y mismo punto de emisión que la sede central: lo que distingue los números
        // es el establecimiento. Antes de FAC-9 el índice único lo impedía (D6).
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                e.OrgId, norte.Id, e.FacturaTypeId, "A1B2C3-D4E5F6-A7B8C9", "002",
                1, 500, new DateOnly(2027, 12, 31)));

        // El servicio se prestó en Norte: lo factura Norte.
        await CobrarAsync(sp, e, e.ExentoItemId, "sr-norte", sede: norte.Id);
        var r = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, e.AccountId, norte.Id, e.FacturaTypeId));

        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.Invoice!.FiscalDocument!.FullNumber.Should().Be("002-002-01-00000001");

        var doc = db.FiscalDocuments.Single(d => d.Id == r.Invoice.FiscalDocument.Id);
        doc.IssuerTaxId.Should().Be("08011999123456", "emite la razón social de la sede, no la de la organización");
        doc.IssuerLegalName.Should().Be("Clínica del Norte S.A.");
        doc.LegalEntityId.Should().Be(razonNorte.Id);
        doc.EstablishmentAddress.Should().Be("Bulevar Kennedy");
    }
}
