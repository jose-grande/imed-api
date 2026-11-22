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
/// Reporte de cajas por sede (UX-Cobro-Anticipado F1): el administrador ve qué se cobró en cada
/// sede y cada cajero, en un rango de días.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CashReportTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid Centro, Guid Norte, Guid CajeroCentro, Guid CajeroNorte, decimal Cobrado);

    /// <summary>
    /// Dos sedes. En el Centro, un cajero cobra una consulta de L 500 en efectivo y su turno queda
    /// abierto. En el Norte, otro cajero abre y cierra sin cobrar, con L 20 de faltante.
    /// </summary>
    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        var cajeroCentro = Guid.NewGuid();
        var cajeroNorte = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var centro = TestSedes.Create(db, orgId, "CENTRO", "Sede Centro", isDefault: true);
        centro.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(centro);
        await db.SaveChangesAsync();
        var norte = TestSedes.Create(db, orgId, "NORTE", "Sede Norte");
        norte.SetFiscalEstablishmentCode("002");
        db.OrganizationLocations.Add(norte);
        await db.SaveChangesAsync();

        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        var exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
        var factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
            new RegisterFiscalAuthorizationCommand(orgId, centro.Id, factura, "CAI-REP", "002", 1, 500, new DateOnly(2027, 12, 31)));

        var crear = sp.GetRequiredService<CreateCashPointHandler>();
        var cajaCentro = await crear.HandleAsync(new CreateCashPointCommand(orgId, centro.Id, "CAJA-C", "Caja Centro", "002", "Services", false, null));
        var cajaNorte = await crear.HandleAsync(new CreateCashPointCommand(orgId, norte.Id, "CAJA-N", "Caja Norte", "002", "Services", false, null));
        var abrir = sp.GetRequiredService<OpenCashSessionHandler>();
        (await abrir.HandleAsync(orgId, cajaCentro.Id, cajeroCentro, 100m, anyLocation: true)).Succeeded.Should().BeTrue();
        var turnoNorte = await abrir.HandleAsync(orgId, cajaNorte.Id, cajeroNorte, 50m, anyLocation: true);

        // Centro: una consulta cobrada en efectivo.
        var item = await sp.GetRequiredService<CreateBillableItemHandler>().HandleAsync(
            new CreateBillableItemCommand(orgId, "CONS", "Consulta general", "Service", exento, 500m, "Either", "Global", null, null, null));
        var cuenta = await sp.GetRequiredService<OpenAccountHandler>().HandleAsync(
            new OpenAccountCommand(orgId, null, "pat-rep-1", "Ana Rivera", null, "Running"));
        await sp.GetRequiredService<CaptureChargeHandler>().HandleAsync(
            new CaptureChargeCommand(orgId, cuenta.Id, item.Id, ChargeSourceKind.Manual, "rep-1", Reason: "prueba"));
        var cobro = await sp.GetRequiredService<AccountCheckoutHandler>().CheckoutAsync(
            orgId, cajeroCentro, cuenta.Id, new AccountCheckoutInput(null, null, [new TenderLine("Cash", 500m, null)]));
        cobro!.Payment!.Succeeded.Should().BeTrue(cobro.Payment.Message);

        // Norte: abre y cierra sin cobrar, con faltante.
        (await sp.GetRequiredService<CloseCashSessionHandler>().HandleAsync(
            new CloseCashSessionCommand(orgId, turnoNorte.Session!.Id, cajeroNorte, 30m, "Faltante de prueba", null)))
            .Succeeded.Should().BeTrue();

        return new Escenario(orgId, centro.Id, norte.Id, cajeroCentro, cajeroNorte, 500m);
    }

    private async Task<CashReportDto> ReporteAsync(Escenario e, Guid? sede = null, Guid? cajero = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var hoy = sp.GetRequiredService<IClinicClock>().Today();
        return await sp.GetRequiredService<GetCashReportHandler>().HandleAsync(e.OrgId, hoy, hoy, sede, cajero);
    }

    [Fact]
    public async Task SinFiltros_separaPorSedeYCajero_yTotaliza()
    {
        var e = await ArrangeAsync();

        var r = await ReporteAsync(e);

        r.Shifts.Should().HaveCount(2);
        r.TotalCollected.Should().Be(e.Cobrado);
        r.TotalDifference.Should().Be(-20m, "el Norte contó 30 y esperaba 50");
        r.OpenShifts.Should().Be(1, "el turno del Centro sigue abierto");
        r.TotalsByMethod.Should().ContainSingle().Which.Method.Should().Be("Cash");

        var centro = r.ByLocation.Single(g => g.Id == e.Centro);
        centro.Name.Should().Be("Sede Centro");
        centro.TotalCollected.Should().Be(e.Cobrado);
        centro.OpenShifts.Should().Be(1);
        r.ByLocation.Single(g => g.Id == e.Norte).TotalDifference.Should().Be(-20m);

        r.ByCashier.Select(g => g.Id).Should().BeEquivalentTo([e.CajeroCentro, e.CajeroNorte]);
        r.ByDay.Should().ContainSingle().Which.TotalCollected.Should().Be(e.Cobrado);
    }

    [Fact]
    public async Task FiltrarPorSede_dejaSoloSusCajas()
    {
        var e = await ArrangeAsync();

        var norte = await ReporteAsync(e, sede: e.Norte);

        norte.Shifts.Should().ContainSingle().Which.LocationName.Should().Be("Sede Norte");
        norte.TotalCollected.Should().Be(0m);
        norte.ByLocation.Should().ContainSingle();
    }

    [Fact]
    public async Task FiltrarPorCajero_dejaSoloSusTurnos()
    {
        var e = await ArrangeAsync();

        var r = await ReporteAsync(e, cajero: e.CajeroCentro);

        r.Shifts.Should().ContainSingle().Which.CashierUserId.Should().Be(e.CajeroCentro);
        r.Shifts[0].ExpectedAmount.Should().BeNull("el turno sigue abierto: el esperado se calcula al cerrar");
        r.Shifts[0].TotalCollected.Should().Be(e.Cobrado, "el jefe sí ve lo cobrado del turno abierto");
    }

    [Fact]
    public async Task UnRangoInvertidoOMuyLargo_seRechaza()
    {
        var e = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var h = scope.ServiceProvider.GetRequiredService<GetCashReportHandler>();

        await FluentActions.Invoking(() => h.HandleAsync(e.OrgId, new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 1), null, null))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*posterior*");
        await FluentActions.Invoking(() => h.HandleAsync(e.OrgId, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), null, null))
            .Should().ThrowAsync<ArgumentException>().WithMessage("*93 días*");
    }

    [Fact]
    public async Task OtraOrganizacion_veElReporteVacio()
    {
        var e = await ArrangeAsync();

        var r = await ReporteAsync(e with { OrgId = Guid.NewGuid() });

        r.Shifts.Should().BeEmpty();
        r.TotalCollected.Should().Be(0m);
    }
}
