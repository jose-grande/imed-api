using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabOrders;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabOrders;

/// <summary>
/// Gateway de órdenes del médico (LIS-RD-09): crear/listar órdenes pasa por
/// imed-api (no directo a HAPI). Cierra la deuda de seguridad del frontend.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrdersGatewayTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(userId, organizationId: Guid.NewGuid().ToString(), realmRoles: ["org_admin"]);
        return fixture.CreateClientWithToken(token);
    }

    [Fact]
    public async Task CreateYListarPorEncounter_porElGateway()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var body = new
        {
            patientFhirId = "pat-go-1",
            encounterFhirId = enc,
            requesterPractitionerId = (string?)null,
            status = "draft",
            priority = "routine",
            loincCode = (string?)null,
            localCode = "CMP",
            localSystem = "http://imed.local/panel",
            codeText = "Perfil metabolico completo",
            codeDisplay = (string?)null,
            occurrenceDateTime = (string?)null,
            reasonText = (string?)null,
            note = (string?)null,
            requisition = enc,
        };

        var create = await client.PostAsJsonAsync("/api/lab-orders", body);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await create.Content.ReadFromJsonAsync<LabOrderDto>();
        dto!.Id.Should().NotBeNullOrWhiteSpace();
        dto.Status.Should().Be("draft");
        dto.LocalCode.Should().Be("CMP");

        var list = await (await client.GetAsync($"/api/lab-orders/by-encounter/{enc}"))
            .Content.ReadFromJsonAsync<LabOrderDto[]>();
        list!.Should().Contain(o => o.Id == dto.Id);
    }

    [Fact]
    public async Task SinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/lab-orders", new { patientFhirId = "p", status = "draft", codeText = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
