using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Catálogo de cobrables y tarifarios por HTTP (B20 y B21).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class BillingCatalogEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Contexto(HttpClient Client, Guid OrgId, Guid Exento, Guid Isv15);

    /// <summary>Organización con el régimen HN ya aprovisionado, que es lo que da los tributos.</summary>
    private async Task<Contexto> ArrangeAsync(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var orgId = Guid.NewGuid();
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(),
            realmRoles: roles.Length == 0 ? ["billing_supervisor"] : roles);
        var client = fixture.CreateClientWithToken(token);

        using var scope = fixture.Factory.Services.CreateScope();
        var provision = scope.ServiceProvider.GetRequiredService<ProvisionFiscalRegimeHandler>();
        var setup = await provision.HandleAsync(orgId, "HN_SAR_CAI");

        return new Contexto(
            client, orgId,
            setup.TaxCodes.First(t => t.Code == "EXENTO").Id,
            setup.TaxCodes.First(t => t.Code == "ISV15").Id);
    }

    private static object ItemBody(string code, string name, Guid taxCodeId, decimal price,
        Guid? labTestId = null, Guid? labPanelId = null) => new
    {
        code, name, kind = "Service", taxCodeId, defaultPrice = price,
        paymentTiming = "Prepaid", component = "Global", labTestId, labPanelId,
        serviceCode = (string?)null,
    };

    [Fact]
    public async Task UnItemExentoYUnoGravado_convivenYCalculanDistinto()
    {
        // Criterio de aceptación 2. El grueso de lo que factura una clínica hondureña es exento
        // —consulta, laboratorio, medicamento— pero la estética paga 15%. La tasa es del ítem.
        var ctx = await ArrangeAsync();

        var consulta = await (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("CONS_GEN", "Consulta general", ctx.Exento, 500m)))
            .Content.ReadFromJsonAsync<BillableItemDto>();

        var estetica = await (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("EST_BOTOX", "Aplicación de toxina botulínica", ctx.Isv15, 4000m)))
            .Content.ReadFromJsonAsync<BillableItemDto>();

        consulta!.TaxCodeId.Should().Be(ctx.Exento);
        estetica!.TaxCodeId.Should().Be(ctx.Isv15);
        consulta.TaxCodeId.Should().NotBe(estetica.TaxCodeId,
            "no puede haber una sola tasa global: la consulta no paga ISV y la estética sí");
    }

    [Fact]
    public async Task UnCobrableConTributoInexistente_seRechaza()
    {
        var ctx = await ArrangeAsync();

        var resp = await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("X", "Algo", Guid.NewGuid(), 100m));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DosCobrablesConElMismoCodigo_seRechaza()
    {
        var ctx = await ArrangeAsync();
        var body = ItemBody("CONS_GEN", "Consulta general", ctx.Exento, 500m);

        (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items", body))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ConDosTarifariosVigentes_elPrecioEsElCorrectoYDiceDeCualSalio()
    {
        // 🎯 Criterio de aceptación 1 de FAC-3, por HTTP.
        var ctx = await ArrangeAsync();
        var item = await (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("CONS_GEN", "Consulta general", ctx.Exento, 500m)))
            .Content.ReadFromJsonAsync<BillableItemDto>();

        var publica = await CrearListaAsync(ctx, "PUB", "Lista pública", "Public");
        var promo = await CrearListaAsync(ctx, "PROMO_AGO", "Promoción de agosto", "Promotional");

        await FijarPrecioAsync(ctx, publica.Id, item!.Id, 450m);
        await FijarPrecioAsync(ctx, promo.Id, item.Id, 380m);

        var precio = await (await ctx.Client.GetAsync($"/api/billing/catalog/items/{item.Id}/price"))
            .Content.ReadFromJsonAsync<PricedItemDto>();

        precio!.Price.Should().Be(380m);
        precio.PriceSource.Should().Be("PromotionalList");
        precio.PriceListName.Should().Be("Promoción de agosto");
        precio.Explanation.Should().Contain("Promoción de agosto",
            "la pantalla tiene que poder decir de qué tarifario salió");
    }

    [Fact]
    public async Task SinTarifarios_seCobraElPrecioBaseYLoDice()
    {
        var ctx = await ArrangeAsync();
        var item = await (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("CONS_GEN", "Consulta general", ctx.Exento, 500m)))
            .Content.ReadFromJsonAsync<BillableItemDto>();

        var precio = await (await ctx.Client.GetAsync($"/api/billing/catalog/items/{item!.Id}/price"))
            .Content.ReadFromJsonAsync<PricedItemDto>();

        precio!.Price.Should().Be(500m);
        precio.PriceSource.Should().Be("ItemDefault");
        precio.Explanation.Should().Contain("ningún tarifario");
    }

    [Fact]
    public async Task UnaPruebaDelLis_seEnlazaAUnCobrable()
    {
        // Criterio de aceptación 3. Es lo que hará posible que el cargo entre solo cuando el
        // laboratorio admite la orden (FAC-4).
        var ctx = await ArrangeAsync();
        await fixture.ProvisionLabCatalogAsync(ctx.OrgId);

        using var scope = fixture.Factory.Services.CreateScope();
        var lab = scope.ServiceProvider.GetRequiredService<ILabCatalogRepository>();
        var prueba = (await lab.SearchTestsAsync(ctx.OrgId, "GLU", null, true, 5)).First();

        var resp = await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("LAB_GLU", "Glucosa", ctx.Exento, 120m, labTestId: prueba.Id));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var creado = await resp.Content.ReadFromJsonAsync<BillableItemDto>();
        creado!.LabTestId.Should().Be(prueba.Id);

        // Y se puede encontrar por la prueba, que es como lo va a buscar la captura de cargos.
        var repo = scope.ServiceProvider.GetRequiredService<IBillingCatalogRepository>();
        var porPrueba = await repo.FindByLabAsync(ctx.OrgId, prueba.Id, null);
        porPrueba!.Code.Should().Be("LAB_GLU");
    }

    [Fact]
    public async Task LaBusqueda_encuentraPorCodigoYPorNombre()
    {
        // De acá come la caja: el cajero teclea el código cuando lo sabe y el nombre cuando no.
        var ctx = await ArrangeAsync();
        await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("CONS_GEN", "Consulta general", ctx.Exento, 500m));

        var porCodigo = await (await ctx.Client.GetAsync("/api/billing/catalog/items?term=CONS"))
            .Content.ReadFromJsonAsync<BillableItemDto[]>();
        var porNombre = await (await ctx.Client.GetAsync("/api/billing/catalog/items?term=general"))
            .Content.ReadFromJsonAsync<BillableItemDto[]>();

        porCodigo!.Should().ContainSingle(i => i.Code == "CONS_GEN");
        porNombre!.Should().ContainSingle(i => i.Code == "CONS_GEN");
    }

    [Fact]
    public async Task ElCajero_consultaPreciosPeroNoDefineElCatalogo()
    {
        var ctx = await ArrangeAsync("cashier");

        (await ctx.Client.GetAsync("/api/billing/catalog/items"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await ctx.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("X", "Algo", ctx.Exento, 10m)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ElCatalogoDeOtraOrganizacion_noSeVe()
    {
        var a = await ArrangeAsync();
        await a.Client.PostAsJsonAsync("/api/billing/catalog/items",
            ItemBody("CONS_GEN", "Consulta de la clínica A", a.Exento, 500m));

        var b = await ArrangeAsync();
        var listado = await (await b.Client.GetAsync("/api/billing/catalog/items"))
            .Content.ReadFromJsonAsync<BillableItemDto[]>();

        listado!.Should().BeEmpty();
    }

    private async Task<PriceListDto> CrearListaAsync(Contexto ctx, string code, string name, string kind)
    {
        var resp = await ctx.Client.PostAsJsonAsync("/api/billing/catalog/price-lists", new
        {
            code, name, kind, validFrom = "2026-01-01", validTo = (string?)null, priority = 0,
            currency = "HNL",
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<PriceListDto>())!;
    }

    private async Task FijarPrecioAsync(Contexto ctx, Guid listId, Guid itemId, decimal price)
    {
        var resp = await ctx.Client.PostAsJsonAsync(
            $"/api/billing/catalog/price-lists/{listId}/items", new
            {
                billableItemId = itemId, price, validFrom = "2026-01-01",
                validTo = (string?)null, minPrice = (decimal?)null, discountPctMax = (decimal?)null,
            });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
