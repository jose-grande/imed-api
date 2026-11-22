using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Billing;
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
/// 🔒 <b>Pruebas de caracterización del flujo de cuenta</b> (UX-Cobro-Anticipado §3.4, fase 0).
///
/// <para>Fijan, de punta a punta, cómo cobra hoy una institución que cobra <b>después</b> del
/// servicio: la consulta abre su cuenta, lo que el médico ordena va a esa cuenta, la Caja la ve
/// pendiente y la cobra en un paso. El cobro anticipado (vender en la Caja, consulta precobrada,
/// combos) se agrega <b>sin afectar este flujo</b> —es una condición del dueño—, y estas pruebas son
/// la forma de comprobarlo: <b>cada fase tiene que pasarlas sin modificarlas.</b></para>
///
/// <para>Si una de estas pruebas falla después de un cambio, el cambio rompió el flujo de cuenta.
/// No se ajusta la prueba: se corrige el cambio.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AccountFlowCharacterizationTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Cardiologia = "394579002";

    private sealed record Escenario(Guid OrgId, Guid UserId, OrganizationLocation Sede, Guid ExentoId, Guid CashPointId);

    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(orgId);

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
            new RegisterFiscalAuthorizationCommand(orgId, sede.Id, factura, "CAI-CAR", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));
        (await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(orgId, caja.Id, userId, 0m, anyLocation: true))
            .Succeeded.Should().BeTrue();

        return new Escenario(orgId, userId, sede, exento, caja.Id);
    }

    private string ConsultaEnCurso(Escenario e, string paciente) => fixture.Hapi.SeedResource(new JsonObject
    {
        ["resourceType"] = "Encounter",
        ["status"] = "in-progress",
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{paciente}" },
        ["participant"] = new JsonArray(new JsonObject { ["actor"] = new JsonObject { ["reference"] = "Practitioner/prac-1" } }),
        ["actualPeriod"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToString("O") },
        ["location"] = new JsonArray(new JsonObject
        {
            ["location"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(e.Sede.Id)}" },
        }),
        ["serviceType"] = new JsonArray(new JsonObject
        {
            ["concept"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://snomed.info/sct", ["code"] = Cardiologia,
            }) },
        }),
        ["type"] = new JsonArray(new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = ConsultationTypeCodes.System, ["code"] = "first-visit",
            }),
        }),
    });

    /// <summary>
    /// Pone precio a los cobrables de los cargos sin precio (la consulta y los exámenes nacen así);
    /// los cargos que lo esperaban lo toman solos.
    /// </summary>
    private async Task PonerPrecioAsync(Escenario e, decimal precio)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var items = await db.BillingCharges.AsNoTracking()
            .Where(c => c.OrganizationId == e.OrgId && c.PricePending)
            .Select(c => c.BillableItemId).Distinct().ToListAsync();
        foreach (var id in items)
        {
            var item = await db.BillableItems.AsNoTracking().SingleAsync(i => i.Id == id);
            await sp.GetRequiredService<UpdateBillableItemHandler>().HandleAsync(
                new UpdateBillableItemCommand(e.OrgId, id, item.Name, e.ExentoId, precio, "Either", true));
        }
    }

    private async Task<IReadOnlyList<BillingAccountDto>> ColaDeLaCajaAsync(Escenario e)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SearchAccountsHandler>()
            .HandleAsync(new SearchAccountsQuery(e.OrgId, Situation: "Pending", LocationId: e.Sede.Id));
    }

    private async Task<AccountCheckoutResult> CobrarEnEfectivoAsync(Escenario e, Guid cuentaId, decimal monto)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
            e.OrgId, e.UserId, cuentaId, new AccountCheckoutInput(null, null, [new TenderLine("Cash", monto, null)])))!;
    }

    [Fact]
    public async Task LaConsulta_abreSuCuenta_apareceEnLaCaja_ySeCobraEnUnPaso()
    {
        var e = await ArrangeAsync();
        var enc = ConsultaEnCurso(e, "pat-car-1");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<EncounterChargeReconciler>().ReconcileAsync(e.OrgId, enc))
                .Should().Be(ConsultationChargeOutcome.Captured, "iniciar la consulta abre su cuenta y la cobra");
        }
        await PonerPrecioAsync(e, 600m);

        var cola = await ColaDeLaCajaAsync(e);
        var cuenta = cola.Should().ContainSingle("la consulta está pendiente en la Caja de su sede").Subject;
        cuenta.EncounterFhirId.Should().Be(enc);
        cuenta.Origin.Should().Be(nameof(BillingAccountOrigin.Encounter));
        cuenta.Billing!.Situation.Should().Be("ToInvoice");
        cuenta.Billing.PendingToInvoice.Should().Be(600m);

        var r = await CobrarEnEfectivoAsync(e, cuenta.Id, 600m);
        r.Invoice!.GrandTotal.Should().Be(600m);
        r.Payment!.Succeeded.Should().BeTrue(r.Payment.Message);
        r.State.TotalDue.Should().Be(0m);
        (await ColaDeLaCajaAsync(e)).Should().BeEmpty("cobrada, sale de la cola");

        // Volver a reconciliar (otra escritura del encuentro) no cobra la consulta otra vez.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<EncounterChargeReconciler>().ReconcileAsync(e.OrgId, enc))
                .Should().Be(ConsultationChargeOutcome.AlreadyCharged);
            (await scope.ServiceProvider.GetRequiredService<AppDbContext>().BillingAccounts
                .CountAsync(a => a.OrganizationId == e.OrgId)).Should().Be(1, "una consulta, una cuenta");
        }
    }

    [Fact]
    public async Task LoQueOrdenaElMedico_vaALaCuentaDeLaConsulta_yUnSoloCobroFacturaTodo()
    {
        var e = await ArrangeAsync();
        var enc = ConsultaEnCurso(e, "pat-car-2");
        var orden = fixture.Hapi.SeedServiceRequest(
            "pat-car-2", loinc: null, "Hemograma completo", encounter: enc, requisition: "ORD-CAR-1",
            status: "active", localCode: "CBC", localSystem: "http://imed.local/panel");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            await sp.GetRequiredService<EncounterChargeReconciler>().ReconcileAsync(e.OrgId, enc);
            (await sp.GetRequiredService<LabOrderChargeReconciler>().ReconcileAsync(e.OrgId, orden)).Should().Be("capturada");
        }
        await PonerPrecioAsync(e, 300m);

        var cuenta = (await ColaDeLaCajaAsync(e)).Should().ContainSingle(
            "la orden del médico no abre otra cuenta: va a la de la consulta").Subject;
        cuenta.Billing!.PendingToInvoice.Should().Be(600m, "consulta + hemograma");

        var r = await CobrarEnEfectivoAsync(e, cuenta.Id, 600m);
        r.Invoice!.Lines.Should().HaveCount(2, "una sola factura con la consulta y el examen");
        r.State.TotalDue.Should().Be(0m);
    }

    [Fact]
    public async Task LaOrdenExterna_abreSuCuentaPorOrden_alAdmitirla_yLaCajaLaCobra()
    {
        var e = await ArrangeAsync();
        var requisicion = $"ORD-{Guid.NewGuid():N}"[..16];
        fixture.Hapi.SeedServiceRequest(
            "pat-car-3", loinc: null, "Hemograma completo", encounter: null!, requisition: requisicion,
            localCode: "CBC", localSystem: "http://imed.local/panel");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<CheckInOrderHandler>().HandleAsync(e.OrgId, requisicion, e.UserId);
        }
        await PonerPrecioAsync(e, 350m);

        var cuenta = (await ColaDeLaCajaAsync(e)).Should().ContainSingle().Subject;
        cuenta.Origin.Should().Be(nameof(BillingAccountOrigin.LabOrder));
        cuenta.OriginRef.Should().Be(requisicion, "la cuenta es de la orden");

        var r = await CobrarEnEfectivoAsync(e, cuenta.Id, 350m);
        r.Payment!.Succeeded.Should().BeTrue(r.Payment.Message);
        r.State.TotalDue.Should().Be(0m);
    }

    [Fact]
    public async Task ElModoDeCobro_esDespuesPorDefecto_yGuardarloNoCambiaNadaMas()
    {
        // Fase 0: el modo de cobro existe pero ninguna conducta depende de él todavía. Las
        // organizaciones existentes quedan en "después", que es el comportamiento de siempre.
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IBillingSettingsRepository>();

        var ajustes = await repo.GetAsync(e.OrgId);
        ajustes.CollectionMode.Should().Be(BillingCollectionMode.AfterService);
        var ventana = ajustes.FollowUpWindowDays;

        ajustes.SetCollectionMode(BillingCollectionMode.BeforeService);
        await repo.SaveAsync(ajustes);

        var leido = await scope.ServiceProvider.GetRequiredService<IBillingSettingsRepository>().GetAsync(e.OrgId);
        leido.CollectionMode.Should().Be(BillingCollectionMode.BeforeService);
        leido.FollowUpWindowDays.Should().Be(ventana);
    }
}
