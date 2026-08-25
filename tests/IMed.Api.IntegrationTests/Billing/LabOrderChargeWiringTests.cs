using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Application.LabReception;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// El cargo entra solo al admitir una orden (FAC-4, origen <c>lab_order</c>).
///
/// <para><b>Por qué esta clase existe aparte de <c>ChargeCaptureTests</c>.</b> Aquellas prueban el
/// mecanismo de captura; estas prueban que alguien lo <b>llame</b>. El handler estuvo escrito y
/// probado durante toda la épica sin un solo llamador — pasaba en verde y no cobraba nada. Un
/// mecanismo correcto que nadie invoca es indistinguible de uno que no existe.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrderChargeWiringTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid UserId, string Requisition, string Encounter);

    /// <summary>
    /// Organización con catálogo del LIS, régimen fiscal, un cobrable enlazado al panel CBC, una
    /// cuenta abierta para el encuentro, y una orden de CBC lista para admitir.
    /// </summary>
    private async Task<Escenario> ArrangeAsync(bool conCobrable = true, bool conCuenta = true)
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var enc = $"enc-{Guid.NewGuid():N}";

        await fixture.ProvisionLabCatalogAsync(orgId);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;

        if (conCobrable)
        {
            var lab = sp.GetRequiredService<ILabCatalogRepository>();
            var panel = (await lab.ListPanelsAsync(orgId)).First(p => p.Code == "CBC");

            await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
                new CreateBillableItemCommand(
                    orgId, "LAB_CBC", "Hemograma completo", "Service", exento, 350m,
                    "Prepaid", "Global", LabTestId: null, LabPanelId: panel.Id, ServiceCode: null));
        }

        if (conCuenta)
        {
            await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
                new OpenAccountCommand(orgId, null, "pat-lab-1", "Juan Pérez", enc, "Episode"));
        }

        // La orden como la escribe el cliente real: panel propio, sin LOINC.
        fixture.Hapi.SeedServiceRequest(
            "pat-lab-1", loinc: null, "Hemograma completo", encounter: enc, requisition: enc,
            localCode: "CBC", localSystem: "http://imed.local/panel");

        return new Escenario(orgId, userId, enc, enc);
    }

    [Fact]
    public async Task AdmitirUnaOrden_generaElCargoSolo()
    {
        // 🎯 El criterio demo-able de FAC-4: el cargo nunca se teclea dos veces.
        var e = await ArrangeAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var checkIn = await sp.GetRequiredService<CheckInOrderHandler>()
            .HandleAsync(e.OrgId, e.Requisition, e.UserId);

        checkIn!.Queued.Should().HaveCount(1, "el CBC se hace en la clínica");
        checkIn.Billing.Should().NotBeNull();
        checkIn.Billing!.Captured.Should().Be(1);
        checkIn.Billing.Unpriced.Should().BeEmpty();

        // Y el cargo está en la cuenta del encuentro, con el precio del catálogo.
        var cuenta = await sp.GetRequiredService<IBillingAccountRepository>()
            .FindByEncounterAsync(e.OrgId, e.Encounter);
        var estado = await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(e.OrgId, cuenta!.Id);

        estado!.Charges.Should().ContainSingle();
        estado.Charges[0].Description.Should().Be("Hemograma completo");
        estado.Charges[0].UnitPrice.Should().Be(350m);
        estado.Charges[0].SourceKind.Should().Be("LabOrder");
        estado.Total.Should().Be(350m);
    }

    [Fact]
    public async Task AdmitirDosVecesLaMismaRequisicion_noDuplicaElCargo()
    {
        // El check-in es idempotente y la caja del paciente también tiene que serlo. Es el mismo
        // botón que un recepcionista aprieta dos veces cuando la pantalla tarda.
        var e = await ArrangeAsync();

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var checkIn = sp.GetRequiredService<CheckInOrderHandler>();

        await checkIn.HandleAsync(e.OrgId, e.Requisition, e.UserId);
        var segunda = await checkIn.HandleAsync(e.OrgId, e.Requisition, e.UserId);

        segunda!.Billing!.Captured.Should().Be(0);
        segunda.Billing.AlreadyCharged.Should().Be(1);

        var cuenta = await sp.GetRequiredService<IBillingAccountRepository>()
            .FindByEncounterAsync(e.OrgId, e.Encounter);
        var estado = await sp.GetRequiredService<GetAccountStatementHandler>()
            .HandleAsync(e.OrgId, cuenta!.Id);

        estado!.Charges.Should().ContainSingle("el paciente no paga dos veces el mismo hemograma");
        estado.Total.Should().Be(350m);
    }

    [Fact]
    public async Task UnExamenSinCobrableEnElCatalogo_seAdmiteIgualYSeReporta()
    {
        // Es deuda del catálogo, no del laboratorio. Lo que no puede pasar es que el examen se
        // haga gratis en silencio: la admisión sigue, pero el faltante se nombra.
        var e = await ArrangeAsync(conCobrable: false);

        using var scope = fixture.Factory.Services.CreateScope();
        var checkIn = await scope.ServiceProvider.GetRequiredService<CheckInOrderHandler>()
            .HandleAsync(e.OrgId, e.Requisition, e.UserId);

        checkIn!.Queued.Should().HaveCount(1, "la orden se admite igual");
        checkIn.Billing!.Captured.Should().Be(0);
        checkIn.Billing.Unpriced.Should().ContainSingle()
            .Which.Should().Be("Hemograma completo");
    }

    [Fact]
    public async Task SinCuentaAbierta_laOrdenSeAdmiteIgual()
    {
        // Costura 4 del modelo: la facturación nunca bloquea al laboratorio. Un laboratorio que
        // no puede recibir una muestra porque no hay cuenta es peor que un cargo pendiente.
        var e = await ArrangeAsync(conCuenta: false);

        using var scope = fixture.Factory.Services.CreateScope();
        var checkIn = await scope.ServiceProvider.GetRequiredService<CheckInOrderHandler>()
            .HandleAsync(e.OrgId, e.Requisition, e.UserId);

        checkIn.Should().NotBeNull();
        checkIn!.Queued.Should().HaveCount(1);
        checkIn.Billing!.Captured.Should().Be(0);
        checkIn.Billing.Unpriced.Should().ContainSingle();
    }

    [Fact]
    public async Task UnExamenReferidoAExterno_noSeCobra()
    {
        // Lo que la clínica no hace, no lo cobra: el paciente lo paga donde se lo hagan.
        var orgId = Guid.NewGuid();
        var enc = $"enc-{Guid.NewGuid():N}";
        await fixture.ProvisionLabCatalogAsync(orgId);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<ProvisionFiscalRegimeHandler>().HandleAsync(orgId, "HN_SAR_CAI");
        await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-ext-1", "Ana", enc, "Episode"));

        // Código que no está en el catálogo de la sede → se refiere a externo.
        fixture.Hapi.SeedServiceRequest(
            "pat-ext-1", loinc: "99999-9", "Examen no ofrecido", encounter: enc, requisition: enc);

        var checkIn = await sp.GetRequiredService<CheckInOrderHandler>()
            .HandleAsync(orgId, enc, Guid.NewGuid());

        checkIn!.External.Should().HaveCount(1);
        checkIn.Queued.Should().BeEmpty();
        checkIn.Billing.Should().BeNull("no había nada que cobrar");
    }
}
