using IMed.Api.Application.Billing;
using IMed.Api.Application.Inventory;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace IMed.Api.IntegrationTests.Inventory;

/// <summary>
/// Organización de prueba para el inventario: una sede principal con código fiscal, régimen
/// hondureño, autorización de facturas, un usuario asignado y un proveedor. Cada prueba arma la
/// suya con una organización nueva, así no se pisan aunque compartan la base.
/// </summary>
internal sealed record InventoryScenario(
    Guid OrgId, Guid LocationId, Guid UserId, Guid FacturaTypeId, Guid ExentoId, Guid Isv15Id, Guid SupplierId)
{
    public static async Task<InventoryScenario> CreateAsync(ImedApiFixture fixture, bool withRegime = true)
    {
        var orgId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();

        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true, taxId: "08019999123456");
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();

        Guid factura = Guid.Empty, exento = Guid.Empty, isv15 = Guid.Empty;
        if (withRegime)
        {
            var setup = await sp.GetRequiredService<ProvisionFiscalRegimeHandler>()
                .HandleAsync(orgId, "HN_SAR_CAI", "08019999123456", "Clínica de Prueba S. de R.L.");
            factura = setup.DocumentTypes.First(t => t.Code == "factura").Id;
            exento = setup.TaxCodes.First(t => t.Code == "EXENTO").Id;
            isv15 = setup.TaxCodes.First(t => t.Code == "ISV15").Id;
            await sp.GetRequiredService<RegisterFiscalAuthorizationHandler>().HandleAsync(
                new RegisterFiscalAuthorizationCommand(
                    orgId, sede.Id, factura, "A1B2C3-D4E5F6-A7B8C9", "002", 1, 500, new DateOnly(2027, 12, 31)));
        }

        var proveedor = await sp.GetRequiredService<SupplierHandlers>().CreateAsync(
            orgId, new SupplierInput("Droguería Nacional", "08011999000001", "Ana", "2222-0000", null, true));

        return new InventoryScenario(orgId, sede.Id, userId, factura, exento, isv15, proveedor.Id);
    }

    public static StockItemInput Medicamento(
        string code, string name, string atc, bool sellable = true, bool otc = false, Guid? taxCodeId = null,
        decimal? minimum = null) =>
        new(code, name, "Medication", "tableta", true, null, sellable, otc, taxCodeId, minimum, true,
            atc, name, "500 mg", "tableta", "oral", null, null, null, null, null,
            [new PresentationInput("Caja x 10", 10m)]);

    public static StockItemInput Insumo(string code, string name, bool sellable = false, Guid? taxCodeId = null) =>
        new(code, name, "Supply", "unidad", false, null, sellable, sellable, taxCodeId, null, true,
            null, null, null, null, null, null, null, null, true, true, null);

    public async Task<StockItemDto> CreateItemAsync(ImedApiFixture fixture, StockItemInput input)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StockItemCommandHandler>().CreateAsync(OrgId, input);
    }

    public async Task<WarehouseDto> GeneralWarehouseAsync(ImedApiFixture fixture)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var bodegas = await scope.ServiceProvider.GetRequiredService<WarehouseHandlers>().ListAsync(OrgId, LocationId, true);
        return bodegas.Single(b => b.IsGeneral);
    }

    public async Task<Guid> ReceiveAsync(
        ImedApiFixture fixture, Guid itemId, string? lot, DateOnly? expiry, decimal quantity, decimal unitCost,
        Guid? warehouseId = null, string? invoice = null, string? presentation = null)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GoodsReceiptHandlers>().CreateAsync(
            OrgId, UserId,
            new GoodsReceiptInput(warehouseId, SupplierId, invoice ?? $"F-{Guid.NewGuid():N}"[..20],
                InDays(-1), null,
                [new GoodsReceiptLineInput(itemId, lot, expiry, presentation, quantity, unitCost)]));
    }

    public async Task SetPriceAsync(ImedApiFixture fixture, Guid billableItemId, decimal price)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var actual = await scope.ServiceProvider.GetRequiredService<IMed.Api.Application.Abstractions.IBillingCatalogRepository>()
            .GetItemAsync(OrgId, billableItemId) ?? throw new InvalidOperationException("No se encontró el cobrable.");
        var r = await scope.ServiceProvider.GetRequiredService<UpdateBillableItemHandler>().HandleAsync(
            new UpdateBillableItemCommand(OrgId, billableItemId, actual.Name, actual.TaxCodeId, price, "Either", true));
        if (r is null)
        {
            throw new InvalidOperationException("No se encontró el cobrable.");
        }
    }

    /// <summary>Un día relativo a hoy en la clínica (Honduras, UTC−6): el inventario corta en el día local.</summary>
    public static DateOnly InDays(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(-6).AddDays(days));
}
