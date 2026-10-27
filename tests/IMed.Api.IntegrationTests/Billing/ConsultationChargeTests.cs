using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Cobro de consultas (plan de salida a producción, D2–D4, A3 y A4): cada consulta abre su cuenta
/// y lleva el cargo de su especialidad y tipo desde que empieza.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class ConsultationChargeTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Cardiologia = "394579002";

    private sealed record Escenario(Guid OrgId, OrganizationLocation Sede, Guid FacturaTypeId);

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
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(
                orgId, sede.Id, factura, "A1B2C3-D4E5F6-A7B8C9", "002", 1, 500, new DateOnly(2027, 12, 31)));
        return new Escenario(orgId, sede, factura);
    }

    private string SeedEncounter(
        Escenario e, string status, string patient = "pat-c1", string? type = "first-visit",
        string? specialty = Cardiologia, string? id = null, bool conSede = true) =>
        fixture.Hapi.SeedResource(BuildEncounter(e, status, patient, type, specialty, id, conSede));

    private static JsonObject BuildEncounter(
        Escenario e, string status, string patient = "pat-c1", string? type = "first-visit",
        string? specialty = Cardiologia, string? id = null, bool conSede = true)
    {
        var enc = new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = status,
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
            ["participant"] = new JsonArray(new JsonObject { ["actor"] = new JsonObject { ["reference"] = "Practitioner/prac-1" } }),
            ["actualPeriod"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToString("O") },
        };
        if (id is not null) { enc["id"] = id; }
        if (conSede)
        {
            enc["location"] = new JsonArray(new JsonObject
            {
                ["location"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(e.Sede.Id)}" },
            });
        }
        if (specialty is not null)
        {
            enc["serviceType"] = new JsonArray(new JsonObject
            {
                ["concept"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://snomed.info/sct", ["code"] = specialty,
                }) },
            });
        }
        if (type is not null)
        {
            enc["type"] = new JsonArray(new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = ConsultationTypeCodes.System, ["code"] = type,
                }),
            });
        }
        return enc;
    }

    private async Task<ConsultationChargeOutcome> ReconciliarAsync(Escenario e, string encounterId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<EncounterChargeReconciler>()
            .ReconcileAsync(e.OrgId, encounterId);
    }

    private async Task<List<(BillingCharge Cargo, BillableItem Item)>> CargosAsync(Escenario e, string encounterId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await (from c in db.BillingCharges.AsNoTracking()
                       join i in db.BillableItems.AsNoTracking() on c.BillableItemId equals i.Id
                       where c.OrganizationId == e.OrgId && c.SourceId == encounterId
                       select new { c, i }).ToListAsync())
            .Select(x => (x.c, x.i)).ToList();
    }

    [Fact]
    public async Task IniciarLaConsulta_abreSuCuenta_yCobraEspecialidadYTipo()
    {
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "in-progress");

        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Captured);

        var cargos = await CargosAsync(e, enc);
        var (cargo, item) = cargos.Single();
        cargo.SourceKind.Should().Be(ChargeSourceKind.Encounter);
        cargo.LocationId.Should().Be(e.Sede.Id, "la consulta se cobra en la sede donde se atendió");
        cargo.PricePending.Should().BeTrue("la especialidad se dio de alta sola y nadie le puso precio");
        item.SpecialtyCode.Should().Be(Cardiologia);
        item.ConsultationType.Should().Be(ConsultationType.FirstVisit);

        using var scope = fixture.Factory.Services.CreateScope();
        var cuenta = await scope.ServiceProvider.GetRequiredService<AppDbContext>().BillingAccounts
            .AsNoTracking().SingleAsync(a => a.Id == cargo.AccountId);
        cuenta.Origin.Should().Be(BillingAccountOrigin.Encounter);
        cuenta.EncounterFhirId.Should().Be(enc);

        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.AlreadyCharged, "reconciliar es idempotente");
    }

    [Fact]
    public async Task PlanificadaPorPreclinica_noSeCobraTodavia()
    {
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "planned");

        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.NotBillable);
        (await CargosAsync(e, enc)).Should().BeEmpty();
    }

    [Fact]
    public async Task CambiarElTipo_reemplazaElCargo_siNoEstaFacturado()
    {
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "in-progress", type: "first-visit", id: "enc-cambio");
        await ReconciliarAsync(e, enc);

        SeedEncounter(e, "in-progress", type: "emergency", id: "enc-cambio");
        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Replaced);

        var cargos = await CargosAsync(e, enc);
        cargos.Should().HaveCount(2);
        cargos.Single(x => x.Cargo.Status == ChargeStatus.Reversed).Item.ConsultationType.Should().Be(ConsultationType.FirstVisit);
        cargos.Single(x => x.Cargo.Status == ChargeStatus.Captured).Item.ConsultationType.Should().Be(ConsultationType.Emergency);
    }

    [Fact]
    public async Task CancelarLaConsulta_reversaElCargo()
    {
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "in-progress", id: "enc-cancelada");
        await ReconciliarAsync(e, enc);

        SeedEncounter(e, "cancelled", id: "enc-cancelada");
        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Reversed);
        (await CargosAsync(e, enc)).Single().Cargo.Status.Should().Be(ChargeStatus.Reversed);
    }

    [Fact]
    public async Task CancelarUnaConsultaYaFacturada_noTocaElCargo()
    {
        // P4: lo facturado se revierte con nota de crédito, que decide una persona.
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "in-progress", id: "enc-facturada");
        await ReconciliarAsync(e, enc);
        var (cargo, item) = (await CargosAsync(e, enc)).Single();

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<UpdateBillableItemHandler>().HandleAsync(new UpdateBillableItemCommand(
                e.OrgId, item.Id, item.Name, item.TaxCodeId, 900m, null, null));
            var f = await sp.GetRequiredService<IssueInvoiceHandler>().HandleAsync(
                new IssueInvoiceCommand(e.OrgId, cargo.AccountId, e.Sede.Id, e.FacturaTypeId, BuyerName: "Ana"));
            f.Succeeded.Should().BeTrue(f.Rejected?.Message);
        }

        SeedEncounter(e, "cancelled", id: "enc-facturada");
        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.InvoicedUntouched);
        (await CargosAsync(e, enc)).Single().Cargo.Status.Should().Be(ChargeStatus.Invoiced);
    }

    [Fact]
    public async Task SinTipo_seProponeControlSiHuboConsultaDeLaEspecialidadEnLaVentana()
    {
        var e = await ArrangeAsync();
        var primera = SeedEncounter(e, "completed", patient: "pat-ctrl", type: null);
        (await ReconciliarAsync(e, primera)).Should().Be(ConsultationChargeOutcome.Captured);
        (await CargosAsync(e, primera)).Single().Item.ConsultationType.Should().Be(ConsultationType.FirstVisit);

        var segunda = SeedEncounter(e, "in-progress", patient: "pat-ctrl", type: null);
        await ReconciliarAsync(e, segunda);
        (await CargosAsync(e, segunda)).Single().Item.ConsultationType.Should().Be(ConsultationType.FollowUp);

        // Otra especialidad no cuenta como antecedente.
        var otra = SeedEncounter(e, "in-progress", patient: "pat-ctrl", type: null, specialty: "394814009");
        await ReconciliarAsync(e, otra);
        (await CargosAsync(e, otra)).Single().Item.ConsultationType.Should().Be(ConsultationType.FirstVisit);
    }

    [Fact]
    public async Task VentanaEnCero_nuncaPropone_control()
    {
        var e = await ArrangeAsync();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IMed.Api.Application.Abstractions.IBillingSettingsRepository>();
            var s = await repo.GetAsync(e.OrgId);
            s.SetFollowUpWindow(0);
            await repo.SaveAsync(s);
        }
        await ReconciliarAsync(e, SeedEncounter(e, "completed", patient: "pat-w0", type: null));
        var segunda = SeedEncounter(e, "in-progress", patient: "pat-w0", type: null);
        await ReconciliarAsync(e, segunda);
        (await CargosAsync(e, segunda)).Single().Item.ConsultationType.Should().Be(ConsultationType.FirstVisit);
    }

    [Fact]
    public async Task SinEspecialidad_seCobraMedicinaGeneral()
    {
        var e = await ArrangeAsync();
        // Sin serviceType: busca el PractitionerRole del médico; si no lo hay, Medicina general (P2).
        var enc = SeedEncounter(e, "in-progress", specialty: null);
        await ReconciliarAsync(e, enc);
        (await CargosAsync(e, enc)).Single().Item.SpecialtyCode.Should().Be(ConsultationTypeCodes.GeneralPracticeSpecialty);
    }

    [Fact]
    public async Task SinSede_noSeCobra_yNoFalla()
    {
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "in-progress", conSede: false);
        (await ReconciliarAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Skipped);
    }

    [Fact]
    public async Task EscribirElEncuentroPorElProxy_encolaElCobro_yElOutboxLoCaptura()
    {
        // De punta a punta: el navegador escribe el Encounter por /fhir, HAPI lo acepta, el proxy
        // encola y el outbox cobra.
        var e = await ArrangeAsync();
        var enc = SeedEncounter(e, "planned", id: "enc-proxy");

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: e.OrgId.ToString(), realmRoles: ["practitioner"]));

        var cuerpo = BuildEncounter(e, "in-progress", id: enc);
        var resp = await client.PutAsync($"/fhir/Encounter/{enc}",
            new StringContent(cuerpo.ToJsonString(), System.Text.Encoding.UTF8, "application/fhir+json"));
        resp.IsSuccessStatusCode.Should().BeTrue(await resp.Content.ReadAsStringAsync());

        await TestExtensions.WaitForAsync(
            probe: () => CargosAsync(e, enc),
            predicate: c => c.Count == 1,
            timeout: TimeSpan.FromSeconds(20),
            description: "cargo de la consulta capturado por el outbox");
    }

    [Fact]
    public void LasEscrituras_seDetectanEnPutPostYBundle()
    {
        var put = new FhirRequestDescriptor("Encounter", FhirVerb.Update, "e1", null, FhirRequestDescriptor.NoQuery);
        EncounterBillingSyncEvent.WrittenEncounterIds(put, null, null).Should().Equal("e1");

        var post = new FhirRequestDescriptor("Encounter", FhirVerb.Create, null, null, FhirRequestDescriptor.NoQuery);
        EncounterBillingSyncEvent.WrittenEncounterIds(post, null, "http://hapi/fhir/Encounter/77/_history/1")
            .Should().Equal("77");

        var bundle = new FhirRequestDescriptor("Bundle", FhirVerb.Create, null, null, FhirRequestDescriptor.NoQuery);
        var body = """{"entry":[{"request":{"method":"PUT","url":"Encounter/9"}},{"request":{"method":"PUT","url":"ServiceRequest/s1"}}]}""";
        EncounterBillingSyncEvent.WrittenEncounterIds(bundle, body, null).Should().Equal("9");
        FhirWrites.WrittenIds("ServiceRequest", bundle, body, null).Should().Equal("s1");

        var lectura = new FhirRequestDescriptor("Encounter", FhirVerb.Read, "e1", null, FhirRequestDescriptor.NoQuery);
        EncounterBillingSyncEvent.WrittenEncounterIds(lectura, null, null).Should().BeEmpty();
    }
}
