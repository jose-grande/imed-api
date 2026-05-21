using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Diagnoses;

[Collection(ImedApiCollection.Name)]
public sealed class DiagnosesSearchTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        // Esperar a que los HostedService seeders carguen el ICD-11 fixture.
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
    public async Task Search_porSubstringEspañol_devuelveCodigos()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/diagnoses/search?q=hipertens");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts.Should().NotBeNull();
        concepts!.Should().NotBeEmpty();
        concepts.Should().Contain(c => c.Code == "BA00");  // Hipertensión esencial
    }

    [Fact]
    public async Task Search_conTildes_matcheaIgualQueSinTildes()
    {
        var client = await AuthedClient();

        var withAccent = await client.GetAsync("/api/diagnoses/search?q=diábetes");
        withAccent.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultsWith = await withAccent.Content.ReadFromJsonAsync<TerminologyConcept[]>();

        var withoutAccent = await client.GetAsync("/api/diagnoses/search?q=diabetes");
        withoutAccent.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultsWithout = await withoutAccent.Content.ReadFromJsonAsync<TerminologyConcept[]>();

        // Unaccent debería normalizar ambos lados — los resultados son idénticos.
        resultsWith.Should().NotBeNull();
        resultsWithout.Should().NotBeNull();
        resultsWith!.Select(c => c.Code).Should().BeEquivalentTo(resultsWithout!.Select(c => c.Code));
        resultsWith.Should().Contain(c => c.Code == "5A11");
    }

    [Fact]
    public async Task Search_caseInsensitive()
    {
        var client = await AuthedClient();

        var lower = await client.GetAsync("/api/diagnoses/search?q=cólera");
        var upper = await client.GetAsync("/api/diagnoses/search?q=CÓLERA");

        var lowerResults = await lower.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        var upperResults = await upper.Content.ReadFromJsonAsync<TerminologyConcept[]>();

        lowerResults!.Should().NotBeEmpty();
        lowerResults.Select(c => c.Code).Should().BeEquivalentTo(upperResults!.Select(c => c.Code));
    }

    [Fact]
    public async Task Search_concepto_traeProperties()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/diagnoses/search?q=cólera");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();

        var cholera = concepts!.FirstOrDefault(c => c.Code == "1A00");
        cholera.Should().NotBeNull();
        cholera!.Display.Should().Be("Cólera", "el prefijo visual '- - - ' se debe strippear");
        cholera.System.Should().Be("http://id.who.int/icd11/mms");
        cholera.Properties.Should().NotBeNull();
        cholera.Properties!.Should().ContainKey("chapter").WhoseValue.Should().Be("01");
        cholera.Properties.Should().ContainKey("foundation-uri");
        cholera.Designations.Should().NotBeNull("display_en está poblado en el fixture");
        cholera.Designations!.Should().Contain(d => d.Language == "en" && d.Value == "Cholera");
    }

    [Fact]
    public async Task Search_termTooShort_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/diagnoses/search?q=h");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts.Should().NotBeNull();
        concepts!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_sinResultados_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/diagnoses/search?q=xyzqwertynonexist");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_sinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/diagnoses/search?q=hipertens");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
