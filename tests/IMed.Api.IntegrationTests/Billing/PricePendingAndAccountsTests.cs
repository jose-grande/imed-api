using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Fase 0 del plan de salida a producción: "sin precio" (D1), alta automática de cobrables
/// (F0.3) y la cuenta de cada acto (D2, F0.4).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PricePendingAndAccountsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string MedicinaGeneral = "394814009";

    private sealed record Escenario(Guid OrgId, Guid LocationId, Guid FacturaTypeId, Guid ExentoId);

    private async Task<Escenario> ArrangeAsync(bool conRegimen = true)
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        if (!conRegimen)
        {
            return new Escenario(orgId, sede.Id, Guid.Empty, Guid.Empty);
        }

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba S. de R.L.");
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "A1B2C3-D4E5F6-A7B8C9", "002", 1, 500, new DateOnly(2027, 12, 31)));

        return new Escenario(orgId, sede.Id, factura, setup.TaxCodes.First(t => t.Code == "EXENTO").Id);
    }

    // ── Alta automática ──────────────────────────────────────────────────

    [Fact]
    public async Task Especialidad_creaTresConsultasSinPrecio_yEsIdempotente()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var prov = scope.ServiceProvider.GetRequiredService<IBillableItemProvisioner>();

        var primera = await prov.EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral);
        var segunda = await prov.EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral);

        primera.Should().HaveCount(3);
        primera.Should().OnlyContain(i => i.PricePending && i.SpecialtyCode == MedicinaGeneral);
        primera.Select(i => i.ConsultationType).Should()
            .BeEquivalentTo(["FirstVisit", "FollowUp", "Emergency"]);
        primera.Select(i => i.Code).Should().Contain($"CONS-{MedicinaGeneral}-1V");
        primera.First(i => i.ConsultationType == "FollowUp").Name.Should().EndWith("— control");
        segunda.Select(i => i.Id).Should().BeEquivalentTo(primera.Select(i => i.Id));

        // Las consultas se cobran exentas por defecto (servicios de salud).
        primera.Should().OnlyContain(i => i.TaxCodeId == e.ExentoId);
    }

    [Fact]
    public async Task Especialidad_enParalelo_noDuplica()
    {
        var e = await ArrangeAsync();

        var tareas = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IBillableItemProvisioner>()
                .EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral);
        });
        var resultados = await Task.WhenAll(tareas);

        resultados.SelectMany(r => r.Select(i => i.Id)).Distinct().Should().HaveCount(3);
    }

    [Fact]
    public async Task SinRegimenFiscal_noCreaNada_yLaReconciliacionLoHaceDespues()
    {
        var e = await ArrangeAsync(conRegimen: false);
        Guid testId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var prueba = LabTestDefinition.Create("HEMO", "Hemograma completo", organizationId: e.OrgId);
            var panel = LabPanel.Create("PERFIL_LIP", "Perfil lipídico", organizationId: e.OrgId);
            db.LabTests.Add(prueba);
            db.LabPanels.Add(panel);
            await db.SaveChangesAsync();
            testId = prueba.Id;

            var prov = scope.ServiceProvider.GetRequiredService<IBillableItemProvisioner>();
            (await prov.EnsureForLabTestAsync(e.OrgId, testId)).Should().BeNull("sin tributos no hay cobrable posible");
            (await prov.EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral)).Should().BeEmpty();

            await scope.ServiceProvider.GetRequiredService<ProvisionFiscalRegimeHandler>()
                .HandleAsync(e.OrgId, "HN_SAR_CAI", "08019999123456", "Clínica");
            // Configurar el régimen ya reconcilia el catálogo (A6): no queda nada por crear.
            (await prov.ReconcileLabAsync(e.OrgId)).Should().Be(0, "el régimen fiscal ya los creó");
        }

        using var verify = fixture.Factory.Services.CreateScope();
        var items = await verify.ServiceProvider.GetRequiredService<AppDbContext>().BillableItems
            .AsNoTracking().Where(i => i.OrganizationId == e.OrgId).ToListAsync();
        items.Should().HaveCount(2).And.OnlyContain(i => i.PricePending);
        items.Single(i => i.LabTestId == testId).Code.Should().Be("LAB-HEMO");
        items.Single(i => i.LabPanelId != null).Code.Should().Be("PAN-PERFIL_LIP");
    }

    [Fact]
    public async Task ElCodigoAutomatico_chocaConUnoCargadoAMano_yTomaSufijo()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var prueba = LabTestDefinition.Create("GLU", "Glucosa", organizationId: e.OrgId);
        db.LabTests.Add(prueba);
        await db.SaveChangesAsync();

        // Alguien ya había cargado un servicio suelto con el código que el sistema generaría.
        await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(new CreateBillableItemCommand(
            e.OrgId, "LAB-GLU", "Glucosa (manual)", "Service", e.ExentoId, 80m, null, null, null, null, null));

        var item = await sp.GetRequiredService<IBillableItemProvisioner>().EnsureForLabTestAsync(e.OrgId, prueba.Id);

        item.Should().NotBeNull();
        item!.Code.Should().Be("LAB-GLU-2");
        item.LabTestId.Should().Be(prueba.Id);
    }

    // ── Sin precio: el cargo entra, la factura no ─────────────────────────

    [Fact]
    public async Task CargoSinPrecio_bloqueaLaFactura_yAlFijarElPrecioSeDestraba()
    {
        var e = await ArrangeAsync();
        Guid itemId, cuentaId;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var items = await sp.GetRequiredService<IBillableItemProvisioner>()
                .EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral);
            itemId = items.First(i => i.ConsultationType == "FirstVisit").Id;

            cuentaId = (await sp.GetRequiredService<IEncounterAccountResolver>()
                .GetOrOpenForEncounterAsync(e.OrgId, "enc-sp-1", "pat-sp-1", "Ana López"))!.Value;

            var cargo = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
                new CaptureChargeCommand(e.OrgId, cuentaId, itemId, ChargeSourceKind.Encounter, "enc-sp-1",
                    LocationId: e.LocationId));
            cargo.Succeeded.Should().BeTrue(cargo.Rejected);
            cargo.Charge!.PricePending.Should().BeTrue();
            cargo.Charge.UnitPrice.Should().Be(0m);

            var bloqueada = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
                new IssueInvoiceCommand(e.OrgId, cuentaId, e.LocationId, e.FacturaTypeId));
            bloqueada.Succeeded.Should().BeFalse();
            bloqueada.Rejected!.Code.Should().Be("cargo_sin_precio");
            bloqueada.Rejected.Message.Should().Contain("Consulta");
        }

        // Fijar el precio por la API, como lo hace el jefe administrativo.
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: e.OrgId.ToString(), realmRoles: ["billing_supervisor"]));

        var pendientes = await client.GetFromJsonAsync<List<BillableItemDto>>(
            "/api/billing/catalog/items/price-pending");
        pendientes!.Select(p => p.Id).Should().Contain(itemId);

        var resp = await client.PatchAsJsonAsync($"/api/billing/catalog/items/{itemId}", new
        {
            name = "Consulta Medicina general — primera vez",
            taxCodeId = e.ExentoId,
            defaultPrice = 650m,
            paymentTiming = "Either",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var editado = await resp.Content.ReadFromJsonAsync<UpdateBillableItemResult>();
        editado!.Item.PricePending.Should().BeFalse();
        editado.RepricedCharges.Should().Be(1);

        using var scope2 = fixture.Factory.Services.CreateScope();
        var r = await scope2.ServiceProvider.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
            new IssueInvoiceCommand(e.OrgId, cuentaId, e.LocationId, e.FacturaTypeId));
        r.Succeeded.Should().BeTrue(r.Rejected?.Message);
        r.Invoice!.GrandTotal.Should().Be(650m);
    }

    [Fact]
    public async Task CargoSinPrecio_noAceptaDescuento()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var item = (await sp.GetRequiredService<IBillableItemProvisioner>()
            .EnsureForSpecialtyAsync(e.OrgId, MedicinaGeneral)).First();
        var cuenta = (await sp.GetRequiredService<IEncounterAccountResolver>()
            .GetOrOpenForEncounterAsync(e.OrgId, "enc-sp-2", "pat-sp-2", null))!.Value;

        var r = await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(e.OrgId, cuenta, item.Id, ChargeSourceKind.Encounter, "enc-sp-2",
                DiscountAmount: 10m, LocationId: e.LocationId));

        r.Succeeded.Should().BeFalse();
        r.Rejected.Should().Contain("todavía no tiene precio");
    }

    // ── La cuenta de cada acto ────────────────────────────────────────────

    [Fact]
    public async Task CuentaDeConsulta_esUnaPorEncuentro_aunqueSePidaEnParalelo()
    {
        var e = await ArrangeAsync();

        var tareas = Enumerable.Range(0, 5).Select(async _ =>
        {
            using var scope = fixture.Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IEncounterAccountResolver>()
                .GetOrOpenForEncounterAsync(e.OrgId, "enc-par-1", "pat-par-1", "Carlos Ruiz");
        });
        var ids = await Task.WhenAll(tareas);

        ids.Should().OnlyContain(id => id.HasValue);
        ids.Distinct().Should().HaveCount(1);

        using var verify = fixture.Factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var cuenta = await db.BillingAccounts.AsNoTracking().SingleAsync(a => a.OrganizationId == e.OrgId);
        cuenta.Origin.Should().Be(BillingAccountOrigin.Encounter);
        cuenta.OriginRef.Should().Be("enc-par-1");
        cuenta.Kind.Should().Be(BillingAccountKind.Episode);
        (await db.Parties.CountAsync(p => p.OrganizationId == e.OrgId)).Should().Be(1, "una sola parte por paciente");
    }

    [Fact]
    public async Task OrdenSinConsulta_abreSuPropiaCuenta_distintaDeLaDeOtraOrden()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IEncounterAccountResolver>();

        var a = await resolver.GetOrOpenStandaloneAsync(e.OrgId, BillingAccountOrigin.LabOrder, "sr-100", "pat-x", "Pedro");
        var a2 = await resolver.GetOrOpenStandaloneAsync(e.OrgId, BillingAccountOrigin.LabOrder, "sr-100", "pat-x", "Pedro");
        var b = await resolver.GetOrOpenStandaloneAsync(e.OrgId, BillingAccountOrigin.LabOrder, "sr-200", "pat-x", "Pedro");
        var venta = await resolver.GetOrOpenStandaloneAsync(e.OrgId, BillingAccountOrigin.PharmacySale, "sr-100", "pat-x", "Pedro");

        a.Should().Be(a2!.Value);
        b.Should().NotBe(a!.Value);
        venta.Should().NotBe(a!.Value, "el mismo id en otro origen es otro acto");

        var invalido = () => resolver.GetOrOpenStandaloneAsync(e.OrgId, BillingAccountOrigin.Encounter, "enc-1", "pat-x", null);
        await invalido.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task CuentaYaFacturada_noRecibeMasCargos()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var resolver = sp.GetRequiredService<IEncounterAccountResolver>();
        var id = (await resolver.GetOrOpenForEncounterAsync(e.OrgId, "enc-cerrada", "pat-c", null))!.Value;

        await sp.GetRequiredService<CloseAccountHandler>().HandleAsync(e.OrgId, id);

        (await resolver.GetOrOpenForEncounterAsync(e.OrgId, "enc-cerrada", "pat-c", null)).Should().BeNull();
    }
}
