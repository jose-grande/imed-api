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
/// Cobro anticipado, fase 3 (UX-Cobro-Anticipado §6.3 y D1):
/// <list type="bullet">
///   <item>La consulta se cobra en caja atada a su cita, y al iniciarla no se cobra otra vez.</item>
///   <item>Solo con modo "antes" o "mixto": con "después" —el de siempre— nada cambia.</item>
///   <item>En modo "antes", una orden impaga no se toma (salvo urgencia o liberación).</item>
/// </list>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PrepaidConsultationTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid UserId, OrganizationLocation Sede, ExternalOrderExam Hemograma);

    private async Task<Escenario> ArrangeAsync(BillingCollectionMode modo)
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
            new RegisterFiscalAuthorizationCommand(orgId, sede.Id, factura, "CAI-PRE", "002", 1, 500, new DateOnly(2027, 12, 31)));
        var caja = await sp.GetRequiredService<CreateCashPointHandler>().HandleAsync(
            new CreateCashPointCommand(orgId, sede.Id, "CAJA1", "Caja 1", "002", "Services", false, null));
        (await sp.GetRequiredService<OpenCashSessionHandler>().HandleAsync(orgId, caja.Id, userId, 0m, anyLocation: true))
            .Succeeded.Should().BeTrue();

        // Precios: consulta de medicina general (primera vez y control) y hemograma.
        var consultas = await sp.GetRequiredService<IBillableItemProvisioner>()
            .EnsureForSpecialtyAsync(orgId, ConsultationTypeCodes.GeneralPracticeSpecialty);
        var actualizar = sp.GetRequiredService<UpdateBillableItemHandler>();
        foreach (var c in consultas)
        {
            await actualizar.HandleAsync(new UpdateBillableItemCommand(orgId, c.Id, c.Name, exento, 500m, "Either", true));
        }
        var panel = (await sp.GetRequiredService<ILabCatalogRepository>().ListPanelsAsync(orgId)).First(x => x.Code == "CBC");
        var cbc = await sp.GetRequiredService<IBillingCatalogRepository>().FindAnyByLabAsync(orgId, null, panel.Id);
        await actualizar.HandleAsync(new UpdateBillableItemCommand(orgId, cbc!.Id, "Hemograma completo", exento, 350m, "Prepaid", true));

        var ajustes = await sp.GetRequiredService<IBillingSettingsRepository>().GetAsync(orgId);
        ajustes.SetCollectionMode(modo);
        await sp.GetRequiredService<IBillingSettingsRepository>().SaveAsync(ajustes);

        return new Escenario(orgId, userId, sede,
            new ExternalOrderExam(panel.LoincCode, panel.Code, LabOrderCodeSystems.Panel, panel.Name, panel.Name));
    }

    private string Cita(Escenario e, string paciente, string estado = "booked") => fixture.Hapi.SeedResource(new JsonObject
    {
        ["resourceType"] = "Appointment",
        ["status"] = estado,
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{paciente}" },
        ["start"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
        ["appointmentType"] = new JsonObject
        {
            ["coding"] = new JsonArray(new JsonObject { ["system"] = ConsultationTypeCodes.System, ["code"] = "first-visit" }),
        },
        ["participant"] = new JsonArray(
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Patient/{paciente}" } },
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = "Practitioner/prac-pre" } },
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(e.Sede.Id)}" } }),
    });

    private string ConsultaDeLaCita(Escenario e, string paciente, string cita) => fixture.Hapi.SeedResource(new JsonObject
    {
        ["resourceType"] = "Encounter",
        ["status"] = "in-progress",
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{paciente}" },
        ["appointment"] = new JsonArray(new JsonObject { ["reference"] = $"Appointment/{cita}" }),
        ["participant"] = new JsonArray(new JsonObject { ["actor"] = new JsonObject { ["reference"] = "Practitioner/prac-pre" } }),
        ["actualPeriod"] = new JsonObject { ["start"] = DateTimeOffset.UtcNow.ToString("O") },
        ["location"] = new JsonArray(new JsonObject
        {
            ["location"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(e.Sede.Id)}" },
        }),
    });

    private async Task<T> ConAsync<T>(Func<IServiceProvider, Task<T>> f)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await f(scope.ServiceProvider);
    }

    private Task<CounterSaleResult> VenderConsultaAsync(Escenario e, string paciente, string cita) =>
        ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().CreateAsync(e.OrgId, e.UserId,
            new CounterSaleInput(paciente, "Paciente", null, null, Appointments: [cita])));

    private Task<AccountCheckoutResult?> CobrarAsync(Escenario e, Guid cuenta, decimal monto) =>
        ConAsync(sp => sp.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
            e.OrgId, e.UserId, cuenta, new AccountCheckoutInput(null, null, [new TenderLine("Cash", monto, null)])));

    private Task<ConsultationChargeOutcome> IniciarConsultaAsync(Escenario e, string encuentro) =>
        ConAsync(sp => sp.GetRequiredService<EncounterChargeReconciler>().ReconcileAsync(e.OrgId, encuentro));

    [Theory]
    [InlineData(BillingCollectionMode.BeforeService)]
    [InlineData(BillingCollectionMode.Mixed)]
    public async Task LaConsultaCobradaEnCaja_noSeCobraOtraVezAlIniciarla(BillingCollectionMode modo)
    {
        var e = await ArrangeAsync(modo);
        var cita = Cita(e, "pat-pre-1");

        var ofrecidas = await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().ListAppointmentsAsync(e.OrgId, "pat-pre-1"));
        ofrecidas.Enabled.Should().BeTrue();
        ofrecidas.Items.Should().ContainSingle().Which.UnitPrice.Should().Be(500m);

        var venta = await VenderConsultaAsync(e, "pat-pre-1", cita);
        (await CobrarAsync(e, venta.AccountId, 500m))!.Payment!.Succeeded.Should().BeTrue();

        var enc = ConsultaDeLaCita(e, "pat-pre-1", cita);
        (await IniciarConsultaAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Prepaid);

        var cargos = await ConAsync(sp => sp.GetRequiredService<AppDbContext>().BillingCharges.AsNoTracking()
            .Where(c => c.OrganizationId == e.OrgId && c.Status != ChargeStatus.Reversed).ToListAsync());
        cargos.Should().ContainSingle("una consulta, un cargo: el de la cita").Which.SourceKind.Should().Be(ChargeSourceKind.Appointment);
        (await ConAsync(sp => sp.GetRequiredService<IBillingAccountRepository>().FindByEncounterAsync(e.OrgId, enc)))
            .Should().BeNull("no se abre la cuenta del encuentro para cobrar algo que ya se pagó");
        (await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().ListAppointmentsAsync(e.OrgId, "pat-pre-1")))
            .Items.Should().BeEmpty("cobrada, ya no se ofrece");
    }

    [Fact]
    public async Task ConModoDespues_laConsultaNoSeVendeEnCaja()
    {
        // Con "después", el cobro al iniciar no reconoce lo precobrado: venderla la cobraría dos veces.
        var e = await ArrangeAsync(BillingCollectionMode.AfterService);
        var cita = Cita(e, "pat-pre-2");

        var ofrecidas = await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().ListAppointmentsAsync(e.OrgId, "pat-pre-2"));
        ofrecidas.Enabled.Should().BeFalse();
        ofrecidas.Reason.Should().Contain("antes");

        await FluentActions.Invoking(() => VenderConsultaAsync(e, "pat-pre-2", cita))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*antes*");
    }

    [Fact]
    public async Task ConModoMixto_unaConsultaSinPrecobrar_seCobraComoSiempre()
    {
        var e = await ArrangeAsync(BillingCollectionMode.Mixed);
        var cita = Cita(e, "pat-pre-3");
        var enc = ConsultaDeLaCita(e, "pat-pre-3", cita);

        (await IniciarConsultaAsync(e, enc)).Should().Be(ConsultationChargeOutcome.Captured);
    }

    [Fact]
    public async Task LaMismaCita_noSeCobraDosVeces_niLaDeOtroPaciente()
    {
        var e = await ArrangeAsync(BillingCollectionMode.BeforeService);
        var cita = Cita(e, "pat-pre-4");
        await VenderConsultaAsync(e, "pat-pre-4", cita);

        await FluentActions.Invoking(() => VenderConsultaAsync(e, "pat-pre-4", cita))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*ya está cobrada*");
        var otra = Cita(e, "pat-pre-5");
        await FluentActions.Invoking(() => VenderConsultaAsync(e, "pat-pre-4", otra))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*otro paciente*");
    }

    [Fact]
    public async Task ConLaConsultaYaIniciada_noSeVende_porqueYaTieneSuCargo()
    {
        var e = await ArrangeAsync(BillingCollectionMode.Mixed);
        var cita = Cita(e, "pat-pre-6", "arrived");
        var enc = ConsultaDeLaCita(e, "pat-pre-6", cita);
        await IniciarConsultaAsync(e, enc);

        await FluentActions.Invoking(() => VenderConsultaAsync(e, "pat-pre-6", cita))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*ya empezó*");
    }

    // ── D1: prepago estricto en la toma de muestra ──────────────────────────────

    private async Task<(CounterSaleResult Venta, Guid Tubo)> VentaDeExamenConTuboAsync(Escenario e, string paciente)
    {
        var venta = await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().CreateAsync(e.OrgId, e.UserId,
            new CounterSaleInput(paciente, "Paciente", [e.Hemograma], null)));
        await ConAsync(sp => sp.GetRequiredService<ComputeCollectionPlanHandler>().HandleAsync(e.OrgId, venta.Requisition!, e.UserId));
        var plan = await ConAsync(sp => sp.GetRequiredService<GenerateLabelsHandler>().HandleAsync(e.OrgId, venta.Requisition!, e.UserId));
        return (venta, plan!.Containers[0].Id);
    }

    private Task<CollectionPlanContainerDto?> IniciarTomaAsync(Escenario e, Guid tubo) =>
        ConAsync(sp => sp.GetRequiredService<AdvanceContainerHandler>().HandleAsync(e.OrgId, tubo, "start", null, e.UserId));

    [Fact]
    public async Task ConModoAntes_unaOrdenImpaga_noSeToma_yPagadaSi()
    {
        var e = await ArrangeAsync(BillingCollectionMode.BeforeService);
        var (venta, tubo) = await VentaDeExamenConTuboAsync(e, "pat-d1-1");

        await FluentActions.Invoking(() => IniciarTomaAsync(e, tubo))
            .Should().ThrowAsync<InvalidOperationException>().WithMessage("*Pendiente de pago*350*");

        (await CobrarAsync(e, venta.AccountId, 350m))!.Payment!.Succeeded.Should().BeTrue();
        (await IniciarTomaAsync(e, tubo)).Should().NotBeNull("pagada, la toma sigue");
    }

    [Theory]
    [InlineData(BillingCollectionMode.AfterService)]
    [InlineData(BillingCollectionMode.Mixed)]
    public async Task FueraDelModoAntes_laPuertaSoloInforma_comoSiempre(BillingCollectionMode modo)
    {
        var e = await ArrangeAsync(modo);
        var (_, tubo) = await VentaDeExamenConTuboAsync(e, "pat-d1-2");

        (await IniciarTomaAsync(e, tubo)).Should().NotBeNull("impaga, pero la toma no se detiene");
    }
    [Theory]
    [InlineData("cancelled")]
    [InlineData("noshow")]
    public async Task LaConsultaPagada_deUnaCitaQueNoSeAtiende_apareceEnExcepciones(string estado)
    {
        var e = await ArrangeAsync(BillingCollectionMode.Mixed);
        var cita = Cita(e, "pat-pre-x");
        var venta = await VenderConsultaAsync(e, "pat-pre-x", cita);
        (await CobrarAsync(e, venta.AccountId, 500m))!.Payment!.Succeeded.Should().BeTrue();

        var reporte = () => ConAsync(sp => sp.GetRequiredService<GetExceptionReportHandler>().HandleAsync(e.OrgId, null, null));
        (await reporte()).CancelledPrepaidAppointments.Should().Be(0, "pagada y vigente: es lo normal");

        // Reprogramar no es una excepción: el pago sigue a la misma cita.
        var reprogramada = fixture.Hapi.GetResource("Appointment", cita)!;
        reprogramada["start"] = DateTimeOffset.UtcNow.AddDays(3).ToString("O");
        fixture.Hapi.SeedResource(reprogramada);
        (await reporte()).CancelledPrepaidAppointments.Should().Be(0);

        var muerta = fixture.Hapi.GetResource("Appointment", cita)!;
        muerta["status"] = estado;
        fixture.Hapi.SeedResource(muerta);

        var r = await reporte();
        r.CancelledPrepaidAppointments.Should().Be(1);
        r.Rows.Should().ContainSingle(x => x.Kind == "CancelledPrepaidAppointment")
            .Which.Amount.Should().Be(500m);
    }

    [Fact]
    public async Task UnaCitaCanceladaSinPagar_noEsExcepcion()
    {
        var e = await ArrangeAsync(BillingCollectionMode.Mixed);
        Cita(e, "pat-pre-y", "cancelled");

        (await ConAsync(sp => sp.GetRequiredService<GetExceptionReportHandler>().HandleAsync(e.OrgId, null, null)))
            .CancelledPrepaidAppointments.Should().Be(0);
    }

    [Fact]
    public async Task LaAgenda_veSiLaConsultaEstaPorPagar_enVentaOPagada()
    {
        var e = await ArrangeAsync(BillingCollectionMode.Mixed);
        var cita = Cita(e, "pat-pre-s");
        var otra = Cita(e, "pat-pre-t");
        Task<AppointmentPaymentStatusDto> Estado() =>
            ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().AppointmentPaymentStatusAsync(e.OrgId, [cita, otra]));

        var antes = await Estado();
        antes.Enabled.Should().BeTrue();
        antes.Items.Should().OnlyContain(i => i.Status == AppointmentPaymentDto.Unpaid);

        var venta = await VenderConsultaAsync(e, "pat-pre-s", cita);
        (await Estado()).Items.Single(i => i.AppointmentId == cita).Status
            .Should().Be(AppointmentPaymentDto.Pending, "está en una venta, pero nadie la cobró");

        (await CobrarAsync(e, venta.AccountId, 500m))!.Payment!.Succeeded.Should().BeTrue();
        var despues = await Estado();
        despues.Items.Single(i => i.AppointmentId == cita).Status.Should().Be(AppointmentPaymentDto.Paid);
        despues.Items.Single(i => i.AppointmentId == otra).Status.Should().Be(AppointmentPaymentDto.Unpaid);
    }

    [Fact]
    public async Task ConModoDespues_laAgendaNoMuestraEstadoDePago()
    {
        var e = await ArrangeAsync(BillingCollectionMode.AfterService);
        var cita = Cita(e, "pat-pre-u");

        var r = await ConAsync(sp => sp.GetRequiredService<CounterSaleHandler>().AppointmentPaymentStatusAsync(e.OrgId, [cita]));
        r.Enabled.Should().BeFalse();
        r.Items.Should().BeEmpty();
    }
}
