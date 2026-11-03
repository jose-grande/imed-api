using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Cajas por sede. Observación de staging: cualquier usuario veía las cajas de todas las sedes y
/// podía abrir turno en una de otra sucursal. Ahora solo quien administra la facturación
/// (<c>billing.supervise</c>) las ve y opera todas; los demás, las de sus sedes asignadas.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class CashPointsBySedeTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Red(Guid OrgId, Guid CajaCeiba, Guid CajaTegucigalpa, Guid Tegucigalpa);

    private async Task<Red> DosSedesConCajaAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var ceiba = TestSedes.Create(db, orgId, "CEIBA", "La Ceiba", isDefault: true);
        ceiba.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(ceiba);
        await db.SaveChangesAsync();
        var tegus = TestSedes.Create(db, orgId, "TGU", "Tegucigalpa");
        tegus.SetFiscalEstablishmentCode("002");
        db.OrganizationLocations.Add(tegus);
        await db.SaveChangesAsync();

        await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        var crear = sp.GetRequiredService<CreateCashPointHandler>();
        var cajaCeiba = await crear.HandleAsync(
            new CreateCashPointCommand(orgId, ceiba.Id, "CAJA-LCE", "Caja La Ceiba", "001", "Services", false, null));
        var cajaTegus = await crear.HandleAsync(
            new CreateCashPointCommand(orgId, tegus.Id, "CAJA-TGU", "Caja Tegucigalpa", "001", "Services", false, null));

        return new Red(orgId, cajaCeiba.Id, cajaTegus.Id, tegus.Id);
    }

    private async Task<Guid> UsuarioDeTegucigalpaAsync(Red red)
    {
        var usuario = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ILocationRepository>()
            .ReplaceAssignmentsAsync(red.OrgId, usuario, [red.Tegucigalpa], red.Tegucigalpa, null);
        return usuario;
    }

    [Fact]
    public async Task Lista_soloLasCajasDeSusSedes_yElAdministradorTodas()
    {
        var red = await DosSedesConCajaAsync();
        var usuario = await UsuarioDeTegucigalpaAsync(red);
        using var scope = fixture.Factory.Services.CreateScope();
        var listar = scope.ServiceProvider.GetRequiredService<ListCashPointsHandler>();

        var suyas = await listar.HandleAsync(red.OrgId, usuario, allLocations: false);
        suyas.Should().ContainSingle().Which.Id.Should().Be(red.CajaTegucigalpa);

        var todas = await listar.HandleAsync(red.OrgId, usuario, allLocations: true);
        todas.Select(c => c.Id).Should().BeEquivalentTo([red.CajaCeiba, red.CajaTegucigalpa]);
    }

    [Fact]
    public async Task AbrirTurno_enCajaDeOtraSede_seRechaza_salvoAlAdministrador()
    {
        var red = await DosSedesConCajaAsync();
        var usuario = await UsuarioDeTegucigalpaAsync(red);
        using var scope = fixture.Factory.Services.CreateScope();
        var abrir = scope.ServiceProvider.GetRequiredService<OpenCashSessionHandler>();

        var ajena = await abrir.HandleAsync(red.OrgId, red.CajaCeiba, usuario, 100m);
        ajena.Succeeded.Should().BeFalse();
        ajena.Code.Should().Be("caja_de_otra_sede");
        ajena.Message.Should().Contain("La Ceiba");

        (await abrir.HandleAsync(red.OrgId, red.CajaTegucigalpa, usuario, 100m))
            .Succeeded.Should().BeTrue("es la caja de su sede");

        var supervisor = Guid.NewGuid();
        (await abrir.HandleAsync(red.OrgId, red.CajaCeiba, supervisor, 100m, anyLocation: true))
            .Succeeded.Should().BeTrue("quien administra la facturación opera cualquier caja");
    }
}
