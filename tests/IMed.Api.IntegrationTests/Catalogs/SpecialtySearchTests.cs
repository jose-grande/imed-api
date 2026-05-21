using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Catalogs;

[Collection(ImedApiCollection.Name)]
public sealed class SpecialtySearchTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Search_porSubstringDelDisplay_caseInsensitive_devuelveMatch()
    {
        await Task.Delay(500); // esperar seeders

        var token = IssueTestToken();
        var client = fixture.CreateClientWithToken(token);

        var resp = await client.GetAsync("/api/specialties/search?q=pediat");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<SpecialtyResultDto[]>();
        results!.Should().Contain(r => r.Code == "394537008");
    }

    [Fact]
    public async Task Search_porPrefijoDeCodigo_devuelveMatch()
    {
        await Task.Delay(500);

        var token = IssueTestToken();
        var client = fixture.CreateClientWithToken(token);

        var resp = await client.GetAsync("/api/specialties/search?q=394814");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<SpecialtyResultDto[]>();
        results!.Should().Contain(r => r.Code == "394814009");
    }

    [Fact]
    public async Task Search_terminoMuyCorto_devuelveListaVacia()
    {
        await Task.Delay(500);

        var token = IssueTestToken();
        var client = fixture.CreateClientWithToken(token);

        var resp = await client.GetAsync("/api/specialties/search?q=p");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var results = await resp.Content.ReadFromJsonAsync<SpecialtyResultDto[]>();
        results!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_sinAuth_devuelve401()
    {
        var unauthClient = fixture.Factory.CreateClient();
        var resp = await unauthClient.GetAsync("/api/specialties/search?q=pediat");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private string IssueTestToken()
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        return fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString(),
            preferredUsername: "med.test");
    }

    private sealed record SpecialtyResultDto(string Code, string DisplayEs, string System);
}
