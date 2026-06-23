using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Recepción por requisición (rediseño R1/R3): una orden-panel se resuelve por
/// encuentro, se expande a sus pruebas agrupadas por tipo de muestra, y se recibe
/// como una muestra N:M. Más la cola de órdenes pendientes (R1-F5).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabRequisitionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: Guid.NewGuid().ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    [Fact]
    public async Task ResolveYReciben_ordenPanel_expandeYAgrupaPorTipoDeMuestra()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        // CMP está sembrado; se ordena por su código local de panel (matchea por Code,
        // independiente del backfill de loinc del panel). Sus pruebas son de química → "sangre".
        var srId = fixture.Hapi.SeedServiceRequest("pat-req-1", "CMP", "Perfil metabolico completo", encounter: enc, requisition: enc);

        // Resolver: la orden-panel se expande y agrupa por tipo de muestra.
        var view = await (await client.GetAsync($"/api/lab/reception/requisition/{enc}"))
            .Content.ReadFromJsonAsync<RequisitionView>();
        view.Should().NotBeNull();
        view!.Groups.Should().NotBeEmpty("la orden-panel debe expandirse a un grupo de tipo de muestra");
        var blood = view.Groups.Should().ContainSingle(g => g.SpecimenTypeCode == "BLOOD").Subject;
        blood.Orders.Should().Contain(o => o.ServiceRequestId == srId);

        // Recibir: una muestra para el grupo (N:M) → accession + Received.
        var receiveBody = new
        {
            requisition = enc,
            groups = new[]
            {
                new
                {
                    specimenTypeCode = blood.SpecimenTypeCode,
                    specimenTypeName = blood.SpecimenTypeName,
                    tubeCode = blood.TubeCode,
                    serviceRequestIds = new[] { srId },
                    collectedDateTime = (string?)null,
                    fastingSatisfied = (bool?)true,
                    reject = false,
                    rejectReason = (string?)null,
                    collectionMethod = (string?)null,
                    collectionBodySite = (string?)null,
                },
            },
        };
        var receiveResp = await client.PostAsJsonAsync("/api/lab/reception/receive-requisition", receiveBody);
        receiveResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await receiveResp.Content.ReadFromJsonAsync<ReceiveGroupResult[]>();
        results.Should().ContainSingle();
        results![0].Status.Should().Be("Received");
        results[0].AccessionNumber.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Pending_listaOrdenesActivasSinRecibir()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest("pat-req-2", "2345-7", "Glucosa", encounter: enc, requisition: enc);

        var pending = await (await client.GetAsync("/api/lab/reception/pending"))
            .Content.ReadFromJsonAsync<PendingOrder[]>();
        pending.Should().NotBeNull();
        pending!.Should().Contain(p => p.ServiceRequestId == srId);
    }
}
