using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabOrders;
using IMed.Api.Domain.Subscriptions;
using IMed.Api.Infrastructure.Lab;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.LabOrders;

/// <summary>
/// Reconciliación de órdenes de laboratorio.
///
/// <para>El cierre de consulta es atómico; la promoción de las órdenes a <c>active</c> no forma
/// parte de él — la hace el navegador después. Entre una cosa y la otra hay una ventana donde la
/// orden se pierde en silencio: un 500, el wifi de la clínica, la pestaña que se cierra. El médico
/// cree que pidió el estudio, el laboratorio nunca lo ve, y nada lo repara.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrderReconciliationTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Crea una orden en <c>draft</c> por el gateway y deja la organización registrada, que es de
    /// donde el barrido saca las particiones a recorrer.
    /// </summary>
    private async Task<(Guid OrgId, string Encounter, string OrderId)> UnaOrdenEnBorradorAsync()
    {
        await Task.Delay(600);
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        using var client = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, organizationId: orgId.ToString(), realmRoles: ["org_admin"]));

        var enc = $"enc-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/lab-orders", new
        {
            patientFhirId = "pat-rec-1",
            encounterFhirId = enc,
            status = "draft",
            localCode = "CBC",
            localSystem = "http://imed.local/panel",
            codeText = "Hemograma completo",
            requisition = enc,
        });
        var dto = await create.Content.ReadFromJsonAsync<LabOrderDto>();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.OrganizationSubscriptions.Add(
            OrganizationSubscription.CreateBootstrapTrial(orgId, "Clinica de prueba", $"org-fhir-{orgId:N}"));
        await db.SaveChangesAsync();

        return (orgId, enc, dto!.Id);
    }

    private static LabOrderReconciliationOptions Options(int graceMinutes = 0) =>
        new() { Enabled = true, GraceMinutes = graceMinutes, MaxPerRun = 100 };

    private async Task<string> EstadoDeLaOrdenAsync(Guid orgId, string orderId)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<GetLabOrderHandler>();
        var dto = await handler.HandleAsync(orgId, orderId);
        return dto!.Status;
    }

    [Fact]
    public async Task UnaOrdenQueQuedoEnBorradorConLaConsultaCerrada_sePromueve()
    {
        var (orgId, enc, orderId) = await UnaOrdenEnBorradorAsync();
        fixture.Hapi.RegisterEncounter(enc, "completed");

        var reconciliation = fixture.Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<LabOrderReconciliation>()
            .Single();

        var promovidas = await reconciliation.RunOnceAsync(Options(), CancellationToken.None);

        promovidas.Should().Be(1);
        (await EstadoDeLaOrdenAsync(orgId, orderId)).Should().Be("active",
            "la consulta se cerró: esa orden ya no es un borrador, es un estudio que alguien espera");
    }

    [Fact]
    public async Task ConLaConsultaAbierta_noSeToca()
    {
        // Es la mitad que importa: un borrador durante la consulta es exactamente lo que tiene que
        // ser. Un barrido que promoviera todo mandaría al laboratorio estudios que el médico está
        // escribiendo y todavía no decidió pedir.
        var (orgId, enc, orderId) = await UnaOrdenEnBorradorAsync();
        fixture.Hapi.RegisterEncounter(enc, "in-progress");

        var reconciliation = fixture.Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<LabOrderReconciliation>()
            .Single();

        var promovidas = await reconciliation.RunOnceAsync(Options(), CancellationToken.None);

        promovidas.Should().Be(0);
        (await EstadoDeLaOrdenAsync(orgId, orderId)).Should().Be("draft");
    }

    [Fact]
    public async Task LaReparacionQuedaAuditada()
    {
        // Una orden que llega al laboratorio tarde y por otro camino no puede ser indistinguible de
        // una que salió bien. Si esto aparece seguido hay algo roto aguas arriba, y el registro es
        // la única forma de enterarse.
        var (orgId, enc, orderId) = await UnaOrdenEnBorradorAsync();
        fixture.Hapi.RegisterEncounter(enc, "completed");

        var reconciliation = fixture.Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<LabOrderReconciliation>()
            .Single();
        await reconciliation.RunOnceAsync(Options(), CancellationToken.None);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registro = await db.AppAuditLog.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == orgId
                && x.Category == LabOrderReconciliation.AuditCategory);

        registro.Should().NotBeNull();
        registro!.Target.Should().Be($"ServiceRequest/{orderId}");
    }

    [Fact]
    public async Task CorrerloDosVeces_noVuelveATocarLoYaReparado()
    {
        // Idempotencia: el barrido corre cada pocos minutos y sobre las mismas organizaciones. Si
        // cada vuelta reescribiera lo ya promovido, inflaría el historial del recurso y llenaría la
        // bitácora de reparaciones que no ocurrieron.
        var (_, enc, _) = await UnaOrdenEnBorradorAsync();
        fixture.Hapi.RegisterEncounter(enc, "completed");

        var reconciliation = fixture.Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<LabOrderReconciliation>()
            .Single();

        (await reconciliation.RunOnceAsync(Options(), CancellationToken.None)).Should().Be(1);
        (await reconciliation.RunOnceAsync(Options(), CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task ElMargenDeGracia_evitaCompetirConLaPromocionDelCliente()
    {
        // Recién cerrada, la orden es del cliente: está promoviéndola en este instante. El barrido
        // no tiene por qué meterse — y si lo hace, los dos escriben el mismo recurso a la vez.
        var (_, enc, _) = await UnaOrdenEnBorradorAsync();
        fixture.Hapi.RegisterEncounter(enc, "completed");

        var reconciliation = fixture.Factory.Services
            .GetRequiredService<IEnumerable<Microsoft.Extensions.Hosting.IHostedService>>()
            .OfType<LabOrderReconciliation>()
            .Single();

        var promovidas = await reconciliation.RunOnceAsync(Options(graceMinutes: 60), CancellationToken.None);

        promovidas.Should().Be(0, "todavía está dentro de la ventana en que el cliente la promueve");
    }
}
