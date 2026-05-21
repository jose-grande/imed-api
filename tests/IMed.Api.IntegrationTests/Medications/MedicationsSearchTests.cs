using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Medications;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Medications;

/// <summary>
/// Tests del endpoint <c>GET /api/medications/search</c> (Épica K1). El
/// catálogo lo seedea <c>MedicationSeeder</c> al startup (~50 ATC entries de
/// medicamentos HN comunes).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class MedicationsSearchTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString());
        return fixture.CreateClientWithToken(token);
    }

    [Fact]
    public async Task Search_sinAuth_retorna401()
    {
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/medications/search?q=paracetamol");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_porGenerico_devuelveResultado()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=paracetamol");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().Contain(r => r.AtcCode == "N02BE01");
        var paracetamol = results.First(r => r.AtcCode == "N02BE01");
        paracetamol.NameGeneric.Should().Be("Paracetamol");
        paracetamol.Category.Should().Be("analgesico");
        paracetamol.System.Should().Be("http://www.whocc.no/atc");
    }

    [Fact]
    public async Task Search_porNombreComercial_devuelveResultado()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=Norvasc");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results.Should().NotBeNull();
        // El seeder asocia Norvasc a Amlodipino (C08CA01).
        results!.Should().Contain(r => r.AtcCode == "C08CA01");
    }

    [Fact]
    public async Task Search_porAtcCodePrefix_devuelveResultado()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=J01");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.AtcCode.Should().StartWith("J01"));
    }

    [Fact]
    public async Task Search_unaccent_matcheaConTildes()
    {
        var client = await AuthedClient();
        var withAccent = await client.GetAsync("/api/medications/search?q=acido");
        var withoutAccent = await client.GetAsync("/api/medications/search?q=ácido");

        var w = await withAccent.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        var wo = await withoutAccent.Content.ReadFromJsonAsync<MedicationResultDto[]>();

        w!.Should().NotBeEmpty();
        w.Select(r => r.AtcCode).Should().BeEquivalentTo(wo!.Select(r => r.AtcCode));
    }

    [Fact]
    public async Task Search_filtraPorCategory()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=cilina&category=antibiotico&limit=50");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results!.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Category.Should().Be("antibiotico"));
    }

    [Fact]
    public async Task Search_termTooShort_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=a");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_respetaLimit()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/medications/search?q=tab&limit=3");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<MedicationResultDto[]>();
        results!.Should().HaveCountLessThanOrEqualTo(3);
    }
}
