using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using IMed.Api.Application.Forms;
using IMed.Api.Domain.Forms;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IMed.Api.IntegrationTests.Forms;

[Collection(ImedApiCollection.Name)]
public sealed class FormDefinitionsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SeedersPopulanPresetsGlobales()
    {
        // El fixture inicializa la app, lo que dispara los HostedService seeders.
        // Esperamos a que terminen (corren on StartAsync).
        await Task.Delay(500);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var globalPresets = await db.FormDefinitions
            .Where(x => x.OrganizationId == null && x.Status == FormStatus.Published)
            .ToListAsync();
        globalPresets.Should().HaveCountGreaterThanOrEqualTo(2);
        globalPresets.Should().Contain(x => x.Code == "general");
        globalPresets.Should().Contain(x => x.Code == "pediatria");
    }

    [Fact]
    public async Task LaVersionNueva_seSuma_sinRetirarLaAnterior_yEsLaQueSeResuelve()
    {
        // Épica P-4: pediatría y prenatal 1.1.0 llevan los antecedentes al widget. La 1.0.0 sigue
        // publicada porque los QR históricos apuntan a ella.
        await Task.Delay(500);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pediatria = await db.FormDefinitions
            .Where(x => x.OrganizationId == null && x.Code == "pediatria" && x.Status == FormStatus.Published)
            .Select(x => x.Version)
            .ToListAsync();
        pediatria.Should().BeEquivalentTo(["1.0.0", "1.1.0"]);

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "med.test");
        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: Guid.NewGuid().ToString(), preferredUsername: "med.test"));

        var dto = await client.GetFromJsonAsync<FormDefinitionResponseDto>("/api/forms/by-specialty/394537008");
        dto!.Version.Should().Be("1.1.0");
        var schema = System.Text.Json.JsonSerializer.Serialize(dto.SchemaJson);
        schema.Should().Contain("\"type\":\"history\"").And.Contain("\"section\":\"immunizations\"");
        schema.Should().NotContain("vaccinations", "la vacunación ya no se pregunta como texto");
    }

    [Fact]
    public async Task GetBySpecialty_general_devuelveElPresetGlobal()
    {
        await Task.Delay(500); // esperar seeders

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "med.test");
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString(),
            preferredUsername: "med.test");
        var client = fixture.CreateClientWithToken(token);

        var resp = await client.GetAsync("/api/forms/by-specialty/general");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<FormDefinitionResponseDto>();
        dto.Should().NotBeNull();
        dto!.Code.Should().Be("general");
        dto.Status.Should().Be("published");
        dto.OrganizationId.Should().BeNull();
    }

    [Fact]
    public async Task GetBySpecialty_codigoInexistente_caeAlGeneralFallback()
    {
        await Task.Delay(500);

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString());
        var client = fixture.CreateClientWithToken(token);

        var resp = await client.GetAsync("/api/forms/by-specialty/cardiologia-pediatrica");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<FormDefinitionResponseDto>();
        dto!.Code.Should().Be("general", "no hay form publicado para esa specialty; cae al general");
    }

    [Fact]
    public async Task CreateForm_creaDraft_yPublishLoTransitaAPublished()
    {
        await Task.Delay(500);

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString(),
            realmRoles: ["org_admin"]);
        var client = fixture.CreateClientWithToken(token);

        var schema = JsonDocument.Parse("""{"version":"1.0","type":"form","items":[]}""").RootElement;
        var mapping = JsonDocument.Parse("""{"version":"1.0","items":[]}""").RootElement;

        var createResp = await client.PostAsJsonAsync("/api/forms", new
        {
            code = "test-custom",
            title = "Form de prueba",
            description = "Solo test",
            specialtyCode = (string?)null,
            version = "1.0.0",
            schemaJson = schema,
            fhirMappingJson = mapping,
            organizationIdOverride = (Guid?)null,
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResp.Content.ReadFromJsonAsync<FormDefinitionResponseDto>();
        created!.Status.Should().Be("draft");

        var publishResp = await client.PostAsync($"/api/forms/{created.Id}/publish", content: null);
        publishResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var published = await publishResp.Content.ReadFromJsonAsync<FormDefinitionResponseDto>();
        published!.Status.Should().Be("published");
        published.PublishedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateForm_duplicadoMismoCodeYVersion_devuelve409()
    {
        await Task.Delay(500);

        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var orgId = Guid.NewGuid();
        var token = fixture.Keycloak.IssueUserToken(userId, organizationId: orgId.ToString());
        var client = fixture.CreateClientWithToken(token);

        var schema = JsonDocument.Parse("""{"version":"1.0","items":[]}""").RootElement;
        var mapping = JsonDocument.Parse("""{"version":"1.0","items":[]}""").RootElement;

        var firstResp = await client.PostAsJsonAsync("/api/forms", new
        {
            code = "dup",
            title = "First",
            version = "1.0.0",
            schemaJson = schema,
            fhirMappingJson = mapping,
        });
        firstResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var dupResp = await client.PostAsJsonAsync("/api/forms", new
        {
            code = "dup",
            title = "Duplicate",
            version = "1.0.0",
            schemaJson = schema,
            fhirMappingJson = mapping,
        });
        dupResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>DTO de respuesta deserializable desde JSON. Spera el shape del FormDefinitionDto.</summary>
    private sealed record FormDefinitionResponseDto(
        Guid Id,
        string Code,
        string? SpecialtyCode,
        string Version,
        string Status,
        string Title,
        string? Description,
        JsonElement SchemaJson,
        JsonElement FhirMappingJson,
        Guid? OrganizationId,
        DateTimeOffset CreatedAt,
        Guid CreatedBy,
        DateTimeOffset? PublishedAt,
        Guid? PublishedBy);
}
