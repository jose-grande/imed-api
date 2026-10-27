using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Domain.Subscriptions;
using IMed.Api.Infrastructure.Encounters;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace IMed.Api.IntegrationTests.Encounters;

/// <summary>
/// Encuentros de preclínica que nadie atendió (pendiente de P-1). Desde P-D2 la preclínica abre el
/// encuentro en <c>planned</c>; si el paciente se va, queda así para siempre. El barrido los cancela
/// cuando ya son de un día anterior de la clínica.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PlannedEncounterSweepTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly PlannedEncounterSweepOptions Options = new() { Enabled = true, MaxPerRun = 100 };

    private PlannedEncounterSweep Sweep() => fixture.Factory.Services
        .GetRequiredService<IEnumerable<IHostedService>>()
        .OfType<PlannedEncounterSweep>()
        .Single();

    private async Task<Guid> OrganizationAsync()
    {
        var orgId = Guid.NewGuid();
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OrganizationSubscriptions.Add(
            OrganizationSubscription.CreateBootstrapTrial(orgId, "Clínica de prueba", $"org-fhir-{orgId:N}"));
        await db.SaveChangesAsync();
        return orgId;
    }

    private string SeedEncounter(string status, DateTimeOffset? lastUpdated)
    {
        var meta = new JsonObject();
        if (lastUpdated is { } at) { meta["lastUpdated"] = at.ToString("o"); }
        return fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = status,
            ["subject"] = new JsonObject { ["reference"] = "Patient/p1" },
            ["appointment"] = new JsonArray(new JsonObject { ["reference"] = "Appointment/a1" }),
            ["meta"] = meta,
        });
    }

    private string StatusOf(string id) => (string)fixture.Hapi.GetResource("Encounter", id)!["status"]!;

    [Fact]
    public async Task UnaPreclinicaDeAyerSinAtender_seCancela_yQuedaRegistrada()
    {
        var orgId = await OrganizationAsync();
        var yesterday = SeedEncounter("planned", DateTimeOffset.UtcNow.AddHours(-30));

        var cancelled = await Sweep().RunOnceAsync(Options, default);

        cancelled.Should().Be(1);
        StatusOf(yesterday).Should().Be("cancelled");
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.AppAuditLog.AnyAsync(x => x.OrganizationId == orgId
            && x.Category == PlannedEncounterSweep.AuditCategory && x.Target == $"Encounter/{yesterday}"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task LaDeHoy_laQueYaSeAtiende_yLaSinFecha_noSeTocan()
    {
        await OrganizationAsync();
        // El paciente puede estar todavía en la sala de espera.
        var today = SeedEncounter("planned", DateTimeOffset.UtcNow.AddMinutes(-5));
        var inProgress = SeedEncounter("in-progress", DateTimeOffset.UtcNow.AddHours(-30));
        // Sin fecha no se puede saber de qué día es; cancelar una consulta viva es peor que esperar.
        var undated = SeedEncounter("planned", null);

        var cancelled = await Sweep().RunOnceAsync(Options, default);

        cancelled.Should().Be(0);
        StatusOf(today).Should().Be("planned");
        StatusOf(inProgress).Should().Be("in-progress");
        StatusOf(undated).Should().Be("planned");
    }

    [Fact]
    public async Task CorrerloDosVeces_noHaceNadaLaSegunda()
    {
        await OrganizationAsync();
        SeedEncounter("planned", DateTimeOffset.UtcNow.AddDays(-3));

        (await Sweep().RunOnceAsync(Options, default)).Should().Be(1);
        (await Sweep().RunOnceAsync(Options, default)).Should().Be(0);
    }
}
