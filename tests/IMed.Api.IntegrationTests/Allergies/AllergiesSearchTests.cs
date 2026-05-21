using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Allergies;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Allergies;

/// <summary>
/// Tests del endpoint <c>GET /api/allergies/search</c> (Épica J1). El catálogo
/// lo seedea <c>AllergySeeder</c> al startup — son ~25 entries SNOMED. Los
/// tests asumen ese fixture sin re-seedearlo manualmente.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AllergiesSearchTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        // Esperar a que el HostedService AllergySeeder cargue el fixture.
        // Idempotente: si ya cargó, no-op.
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
        var resp = await client.GetAsync("/api/allergies/search?q=penicilina");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_porDisplay_devuelveCodigo()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=penicilina");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().Contain(r => r.Code == "294505008");
        var penicillin = results.First(r => r.Code == "294505008");
        penicillin.DisplayEs.Should().Contain("penicilina", "el seeder fija el display en español");
        penicillin.Category.Should().Be("medication");
        penicillin.System.Should().Be("http://snomed.info/sct");
    }

    [Fact]
    public async Task Search_porCodigoSnomed_devuelvePrefixMatch()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=294505008");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().Contain(r => r.Code == "294505008");
    }

    [Fact]
    public async Task Search_caseInsensitive()
    {
        var client = await AuthedClient();
        var lower = await client.GetAsync("/api/allergies/search?q=mani");
        var upper = await client.GetAsync("/api/allergies/search?q=MANI");

        var lowerResults = await lower.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        var upperResults = await upper.Content.ReadFromJsonAsync<AllergyResultDto[]>();

        lowerResults!.Should().NotBeEmpty();
        lowerResults.Select(r => r.Code).Should().BeEquivalentTo(upperResults!.Select(r => r.Code));
    }

    [Fact]
    public async Task Search_filtraPorCategory()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=alergia&category=food&limit=50");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.Category.Should().Be("food"));
    }

    [Fact]
    public async Task Search_termTooShort_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=a");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results.Should().NotBeNull();
        results!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_sinResultados_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=xyzqwertynonexist");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_respetaLimit()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/allergies/search?q=alergia&limit=3");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<AllergyResultDto[]>();
        results!.Should().HaveCountLessThanOrEqualTo(3);
    }
}
