using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Labs;

[Collection(ImedApiCollection.Name)]
public sealed class LabsSearchTests(ImedApiFixture fixture) : IAsyncLifetime
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
    public async Task Search_porSubstringEspanol_devuelveCodigosLoinc()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/labs/search?q=hemograma");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts.Should().NotBeNull();
        concepts!.Should().Contain(c => c.Code == "58410-2", "el fixture esMX traduce 58410-2 como 'Hemograma completo'");
    }

    [Fact]
    public async Task Search_porPrefijoDeCodigo_funciona()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/labs/search?q=718");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts!.Should().Contain(c => c.Code == "718-7");
    }

    [Fact]
    public async Task Search_concepto_traePropertiesYDesignations()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/labs/search?q=hemoglobina");
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();

        var hemoglobina = concepts!.FirstOrDefault(c => c.Code == "718-7");
        hemoglobina.Should().NotBeNull();
        hemoglobina!.System.Should().Be("http://loinc.org");
        hemoglobina.Display.Should().Contain("Hemoglobina", "el display primario viene de esMX28");
        hemoglobina.Properties.Should().NotBeNull();
        hemoglobina.Properties!.Should().ContainKey("class").WhoseValue.Should().Be("HEM/BC");
        hemoglobina.Properties.Should().ContainKey("designations", "el variant esES12 está cargado como designation alternativa");
        // Los codes traducidos NO deben tener translation-pending
        hemoglobina.Properties.Should().NotContainKey("translation-pending");
        // Designation con DisplayEn
        hemoglobina.Designations.Should().NotBeNull();
        hemoglobina.Designations!.Should().Contain(d => d.Language == "en");
    }

    [Fact]
    public async Task Search_codigoSinTraduccion_caeAlInglesConFlag()
    {
        var client = await AuthedClient();
        // 33914-3 (GFR) está en el main pero NO en esMX28 — debe quedar en inglés con flag.
        var resp = await client.GetAsync("/api/labs/search?q=glomerular");
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        var gfr = concepts!.FirstOrDefault(c => c.Code == "33914-3");
        gfr.Should().NotBeNull();
        gfr!.Display.Should().Contain("Glomerular", "fallback a inglés cuando no hay traducción ES");
        gfr.Properties.Should().ContainKey("translation-pending").WhoseValue.Should().Be("true");
    }

    [Fact]
    public async Task Search_codigoDeprecated_noAparece()
    {
        var client = await AuthedClient();
        // 6298-4 está en el main como STATUS=DEPRECATED — debe estar excluido del seed.
        var resp = await client.GetAsync("/api/labs/search?q=potasio");
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts!.Should().NotContain(c => c.Code == "6298-4");
        concepts.Should().Contain(c => c.Code == "2823-3", "la versión ACTIVE sí aparece");
    }

    [Fact]
    public async Task Search_termTooShort_retornaArrayVacio()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/labs/search?q=h");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var concepts = await resp.Content.ReadFromJsonAsync<TerminologyConcept[]>();
        concepts!.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_sinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/labs/search?q=hemoglobina");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
