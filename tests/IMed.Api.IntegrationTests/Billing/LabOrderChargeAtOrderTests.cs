using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// El examen se cobra al ordenarlo (D5, A5): cuando el médico emite la orden de la consulta, el
/// cargo entra a la cuenta de la consulta, en su sede, antes de que el paciente llegue al laboratorio.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrderChargeAtOrderTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record Escenario(Guid OrgId, Guid SedeId, string Encounter);

    private async Task<Escenario> ArrangeAsync()
    {
        var orgId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(orgId);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<AppDbContext>();
        var sede = TestSedes.Create(db, orgId, "NORTE", "Sede norte", isDefault: true);
        db.OrganizationLocations.Add(sede);
        await db.SaveChangesAsync();
        await sp.GetRequiredService<ProvisionFiscalRegimeHandler>().HandleAsync(orgId, "HN_SAR_CAI");

        var enc = fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = "in-progress",
            ["subject"] = new JsonObject { ["reference"] = "Patient/pat-ord" },
            ["location"] = new JsonArray(new JsonObject
            {
                ["location"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(sede.Id)}" },
            }),
        });
        return new Escenario(orgId, sede.Id, enc);
    }

    private string Orden(Escenario e, string status) => fixture.Hapi.SeedServiceRequest(
        "pat-ord", loinc: null, "Hemograma completo", encounter: e.Encounter, requisition: "ORD-1",
        status: status, localCode: "CBC", localSystem: "http://imed.local/panel");

    private async Task<string> ReconciliarAsync(Escenario e, string orden)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<LabOrderChargeReconciler>().ReconcileAsync(e.OrgId, orden);
    }

    private async Task<List<BillingCharge>> CargosAsync(Escenario e, string orden)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().BillingCharges.AsNoTracking()
            .Where(c => c.OrganizationId == e.OrgId && c.SourceId == orden).ToListAsync();
    }

    [Fact]
    public async Task OrdenEmitida_seCobraEnLaCuentaYLaSedeDeLaConsulta()
    {
        var e = await ArrangeAsync();
        var orden = Orden(e, "active");

        (await ReconciliarAsync(e, orden)).Should().Be("capturada");

        var cargo = (await CargosAsync(e, orden)).Single();
        cargo.SourceKind.Should().Be(ChargeSourceKind.LabOrder);
        cargo.LocationId.Should().Be(e.SedeId);
        cargo.PricePending.Should().BeTrue("el panel nació sin precio");

        using var scope = fixture.Factory.Services.CreateScope();
        var cuenta = await scope.ServiceProvider.GetRequiredService<IBillingAccountRepository>()
            .FindByEncounterAsync(e.OrgId, e.Encounter);
        cuenta!.Id.Should().Be(cargo.AccountId, "la orden va a la cuenta de su consulta");

        (await ReconciliarAsync(e, orden)).Should().Be("ya-cobrada");
    }

    [Fact]
    public async Task OrdenEnBorrador_noSeCobra()
    {
        var e = await ArrangeAsync();
        var orden = Orden(e, "draft");

        (await ReconciliarAsync(e, orden)).Should().Be("no-cobra-aun");
        (await CargosAsync(e, orden)).Should().BeEmpty();
    }

    [Fact]
    public async Task OrdenRevocada_reversaElCargo()
    {
        var e = await ArrangeAsync();
        var orden = Orden(e, "active");
        await ReconciliarAsync(e, orden);

        fixture.Hapi.SetServiceRequestStatus(orden, "revoked");
        (await ReconciliarAsync(e, orden)).Should().Be("reversada");
        (await CargosAsync(e, orden)).Single().Status.Should().Be(ChargeStatus.Reversed);
    }

    [Fact]
    public async Task AlRecibirlaEnElLaboratorio_noSeCobraDosVeces()
    {
        // El cobro de la recepción sigue existiendo para las órdenes externas; para la orden de una
        // consulta ya cobrada al emitirse, la recepción encuentra el cargo y no agrega otro.
        var e = await ArrangeAsync();
        var orden = Orden(e, "active");
        await ReconciliarAsync(e, orden);

        using var scope = fixture.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var snapshot = await sp.GetRequiredService<IHapiFhirClient>().GetLabOrderAsync(orden, $"org-{e.OrgId}");
        var cargo = await LabOrderChargeDescriber.DescribeAsync(
            sp.GetRequiredService<ILabCatalogRepository>(), e.OrgId, snapshot!, default);
        var r = await sp.GetRequiredService<CaptureLabOrderChargesHandler>().HandleAsync(e.OrgId, [cargo], e.SedeId);

        r.AlreadyCharged.Should().Be(1);
        (await CargosAsync(e, orden)).Should().ContainSingle();
    }
}
