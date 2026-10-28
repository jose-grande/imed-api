using FluentAssertions;
using IMed.Api.Application.Inventory;
using IMed.Api.Domain.Inventory;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Inventory;

/// <summary>
/// La lista de unidades base: se siembra sola, el artículo solo acepta unidades de la lista y un
/// renombre se propaga a los artículos.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class InventoryUnitTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private T Get<T>(IServiceScope scope) where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task LaLista_seSiembraSola_eIncluyeLasUnidadesQueYaUsanLosArticulos()
    {
        var e = await InventoryScenario.CreateAsync(fixture, withRegime: false);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            // Un artículo de cuando la unidad era texto libre.
            var db = Get<AppDbContext>(scope);
            db.StockItems.Add(StockItem.Create(e.OrgId, "VIEJO-1", "Gasa", StockItemKind.Supply, "Paquete", false, null));
            await db.SaveChangesAsync();
        }

        using var s = fixture.Factory.Services.CreateScope();
        var unidades = await Get<InventoryUnitHandlers>(s).ListAsync(e.OrgId, activeOnly: true);

        unidades.Select(u => u.Name).Should().Contain(InventoryUnit.Defaults).And.Contain("paquete");
        (await Get<InventoryUnitHandlers>(s).ListAsync(e.OrgId, activeOnly: true))
            .Should().HaveCount(unidades.Count, "volver a listar no duplica");
    }

    [Fact]
    public async Task ElArticulo_soloAceptaUnidadesDeLaLista_yActivas()
    {
        var e = await InventoryScenario.CreateAsync(fixture, withRegime: false);

        var fuera = () => e.CreateItemAsync(fixture, InventoryScenario.Insumo("INS-1", "Gasa") with { BaseUnit = "tabs" });
        await fuera.Should().ThrowAsync<ArgumentException>().WithMessage("*no está en la lista*");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var h = Get<InventoryUnitHandlers>(scope);
            await h.CreateAsync(e.OrgId, new InventoryUnitInput("  Paquete "));
            var duplicada = () => h.CreateAsync(e.OrgId, new InventoryUnitInput("paquete"));
            await duplicada.Should().ThrowAsync<ArgumentException>().WithMessage("*ya está en la lista*");
        }
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("INS-2", "Gasa") with { BaseUnit = "Paquete" });
        item.BaseUnit.Should().Be("paquete");

        // Desactivada: no entra en artículos nuevos, pero el que ya la tiene se sigue editando.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var h = Get<InventoryUnitHandlers>(scope);
            var paquete = (await h.ListAsync(e.OrgId, false)).Single(u => u.Name == "paquete");
            await h.UpdateAsync(e.OrgId, paquete.Id, new InventoryUnitInput("paquete", Active: false));
        }
        var nueva = () => e.CreateItemAsync(fixture, InventoryScenario.Insumo("INS-3", "Gasa") with { BaseUnit = "paquete" });
        await nueva.Should().ThrowAsync<ArgumentException>().WithMessage("*desactivada*");

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var editado = await Get<StockItemCommandHandler>(scope).UpdateAsync(
                e.OrgId, item.Id, InventoryScenario.Insumo("INS-2", "Gasa estéril") with { BaseUnit = "paquete" });
            editado!.Name.Should().Be("Gasa estéril");
        }
    }

    [Fact]
    public async Task Renombrar_unaUnidad_cambiaLaDeLosArticulos_yNoChocaConOtra()
    {
        var e = await InventoryScenario.CreateAsync(fixture, withRegime: false);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            await Get<InventoryUnitHandlers>(scope).CreateAsync(e.OrgId, new InventoryUnitInput("tab"));
        }
        var item = await e.CreateItemAsync(fixture, InventoryScenario.Insumo("INS-1", "Algo") with { BaseUnit = "tab" });

        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var h = Get<InventoryUnitHandlers>(scope);
            var tab = (await h.ListAsync(e.OrgId, false)).Single(u => u.Name == "tab");

            var choca = () => h.UpdateAsync(e.OrgId, tab.Id, new InventoryUnitInput("tableta"));
            await choca.Should().ThrowAsync<ArgumentException>().WithMessage("*ya está en la lista*");

            (await h.UpdateAsync(e.OrgId, tab.Id, new InventoryUnitInput("tira")))!.Name.Should().Be("tira");
        }

        using var verify = fixture.Factory.Services.CreateScope();
        (await Get<AppDbContext>(verify).StockItems.AsNoTracking().SingleAsync(i => i.Id == item.Id))
            .BaseUnit.Should().Be("tira");
    }
}
