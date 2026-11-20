using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// El código de establecimiento se captura con el CAI, como viene en el papel del SAR. Observación
/// local: registrado el CAI sin código, no había dónde cargarlo y ninguna factura salía.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AuthorizationEstablishmentTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(Guid Org, Guid Sede, Guid Factura)> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();
        var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
            .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba");
        return (orgId, sede.Id, setup.DocumentTypes.First(t => t.Code == "factura").Id);
    }

    private static RegisterFiscalAuthorizationCommand Cai(Guid org, Guid sede, Guid tipo, string? estab) =>
        new(org, sede, tipo, "CAI-QA", "420", 1, 500, new DateOnly(2027, 12, 31), estab);

    [Fact]
    public async Task RegistrarElCai_conEstablecimiento_loGuardaEnLaSede()
    {
        var (org, sede, tipo) = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(Cai(org, sede, tipo, "001"));

        (await sp.GetRequiredService<IInvoiceRepository>().GetEstablishmentCodeAsync(org, sede))
            .Should().Be("001");
    }

    [Fact]
    public async Task UnEstablecimientoInvalido_noDejaElCaiRegistrado()
    {
        var (org, sede, tipo) = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var registrar = () => sp.GetRequiredService<RegisterFiscalAuthorizationHandler>()
            .HandleAsync(Cai(org, sede, tipo, "1A"));

        await registrar.Should().ThrowAsync<ArgumentException>().WithMessage("*3 dígitos*");
        (await sp.GetRequiredService<IFiscalRepository>().ListAuthorizationsAsync(org)).Should().BeEmpty();
    }

    [Fact]
    public async Task SinEstablecimiento_noTocaElDeLaSede()
    {
        var (org, sede, tipo) = await ArrangeAsync();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var handler = sp.GetRequiredService<RegisterFiscalAuthorizationHandler>();

        await handler.HandleAsync(Cai(org, sede, tipo, "002"));
        await handler.HandleAsync(Cai(org, sede, tipo, null) with { Cai = "CAI-2" });

        (await sp.GetRequiredService<IInvoiceRepository>().GetEstablishmentCodeAsync(org, sede))
            .Should().Be("002");
    }
}
