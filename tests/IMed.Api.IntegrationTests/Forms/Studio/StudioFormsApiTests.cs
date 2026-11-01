using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Forms.Studio;
using IMed.Api.Domain.Forms;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Fixture = IMed.Api.IntegrationTests.Forms.Studio.StudioDocumentTests.Fixture;

namespace IMed.Api.IntegrationTests.Forms.Studio;

/// <summary>
/// Endpoints del studio de formularios (catálogo, resolve por code y procesamiento de documentos) y
/// el proveedor que lee el catálogo de la base. Cada prueba parte de un tipo de formulario de prueba
/// (<c>studio-test</c>) con su catálogo y su formulario base, insertados con las entidades.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class StudioFormsApiTests(ImedApiFixture fixture) : IAsyncLifetime
{
    private const string Code = StudioDocumentTests.Code;
    private const string ExtensionBase = "https://example.org/fhir/StructureDefinition/patient-custom-field";

    public async Task InitializeAsync()
    {
        await fixture.ResetStateAsync();
        await SeedTestFormAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Deja listo el tipo de formulario de prueba: su catálogo con sus campos y su formulario base
    /// (global, publicado). La base de pruebas se comparte entre pruebas, así que solo inserta lo que falta.
    /// </summary>
    private async Task SeedTestFormAsync()
    {
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            if (!await db.FormCatalogs.AnyAsync(c => c.FormCode == Code))
            {
                var catalog = FormCatalog.Create(Code, "1.0.0", "Formulario de prueba", """["Patient"]""");
                db.FormCatalogs.Add(catalog);
                db.FormCatalogFields.AddRange(Fixture.Fields.Select((def, index) => FormCatalogField.Create(
                    catalog.Id,
                    def.Key,
                    index + 1,
                    def.Widget,
                    def.Editable,
                    $$"""{"label":"{{def.Key}}"}""",
                    def.Binding,
                    core: def.Core,
                    custom: def.Custom)));
            }

            if (!await db.FormDefinitions.AnyAsync(f => f.Code == Code && f.OrganizationId == null))
            {
                var document = Fixture.DocumentJson();
                var mapping = StudioMappingBuilder.Build(JsonNode.Parse(document)!, Fixture.Catalog, ExtensionBase);
                var preset = FormDefinition.CreateDraft(Code, "Formulario de prueba", document, mapping, Guid.NewGuid());
                preset.Publish(Guid.NewGuid());
                db.FormDefinitions.Add(preset);
            }

            await db.SaveChangesAsync();
        }

        fixture.Factory.Services.GetRequiredService<IFieldCatalogProvider>().Invalidate();
    }

    private HttpClient ClientFor(Guid organizationId, params string[] realmRoles)
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(userId, organizationId: organizationId.ToString(), realmRoles: realmRoles);
        return fixture.CreateClientWithToken(token);
    }

    private static async Task<FormResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<FormResponse>())!;

    /// <summary>El documento del formulario base global, para usarlo como base de formularios de una organización.</summary>
    private static async Task<JsonElement> GlobalPresetDocumentAsync(HttpClient client)
    {
        var response = await client.GetAsync($"/api/forms/resolve/{Code}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await ReadAsync(response)).SchemaJson;
    }

    private static Task<HttpResponseMessage> CreateAsync(HttpClient client, string version, JsonElement schema, string title = "Registro") =>
        client.PostAsJsonAsync("/api/forms", new
        {
            code = Code,
            title,
            version,
            schemaJson = schema,
            fhirMappingJson = JsonDocument.Parse("""{"items":[]}""").RootElement,
        });

    private static int ItemCount(JsonElement mapping) => mapping.GetProperty("items").GetArrayLength();

    private static FormCatalogField FieldOf(Guid catalogId, string key, int order, bool active = true, bool core = false) =>
        FormCatalogField.Create(
            catalogId, key, order, "text",
            editable: """["label"]""",
            defaults: """{"label":"Campo"}""",
            binding: """{"mapsTo":"Patient.name","extractor":"human-name"}""",
            core: core,
            active: active);

    //#region Catálogo

    [Fact]
    public async Task ElCatalogoSeSirveEnJsonConSusCamposEnOrdenYUnCodigoDesconocidoDa404()
    {
        var client = ClientFor(Guid.NewGuid());

        var ok = await client.GetAsync($"/api/form-catalog/{Code}");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        ok.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var catalog = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var root = catalog.RootElement;
        root.GetProperty("form").GetString().Should().Be(Code);
        root.GetProperty("title").GetString().Should().Be("Formulario de prueba");
        root.GetProperty("resourceTypes").EnumerateArray().Select(r => r.GetString()).Should().Equal("Patient");
        root.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("key").GetString())
            .Should().Equal(Fixture.Fields.Select(f => f.Key));

        var birthDate = root.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("key").GetString() == "birthDate");
        birthDate.GetProperty("core").GetBoolean().Should().BeTrue();
        birthDate.TryGetProperty("custom", out _).Should().BeFalse("las banderas en falso se omiten");
        birthDate.GetProperty("binding").GetProperty("extractor").GetString().Should().Be("primitive");

        (await client.GetAsync("/api/form-catalog/no-existe")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ElListadoDeCatalogosMuestraLosTiposDeFormulario()
    {
        var response = await ClientFor(Guid.NewGuid()).GetAsync("/api/form-catalog");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var list = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entry = list.RootElement.EnumerateArray().Single(e => e.GetProperty("formCode").GetString() == Code);
        entry.GetProperty("version").GetString().Should().Be("1.0.0");
        entry.GetProperty("title").GetString().Should().Be("Formulario de prueba");
        entry.GetProperty("resourceTypes").EnumerateArray().Select(r => r.GetString()).Should().Equal("Patient");
    }

    [Fact]
    public async Task ElCatalogoRequiereAutenticacion()
    {
        var anonymous = fixture.Factory.CreateClient();

        (await anonymous.GetAsync($"/api/form-catalog/{Code}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/form-catalog")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/forms/resolve/{Code}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ElProveedorUsaLaVersionActivaMasAltaDeCadaTipoYSusCamposActivosEnOrden()
    {
        var code = $"test-{Guid.NewGuid():N}"[..20];
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var nine = FormCatalog.Create(code, "1.9.0", "Nueve", "[]");
            var ten = FormCatalog.Create(code, "1.10.0", "Diez", "[]");
            var inactive = FormCatalog.Create(code, "2.0.0", "Inactivo", "[]", active: false);
            db.FormCatalogs.AddRange(nine, ten, inactive);
            db.FormCatalogFields.AddRange(
                FieldOf(nine.Id, "viejo", 1),
                FieldOf(ten.Id, "segundo", 2),
                FieldOf(ten.Id, "primero", 1, core: true),
                FieldOf(ten.Id, "apagado", 3, active: false),
                FieldOf(inactive.Id, "otro", 1));
            await db.SaveChangesAsync();
        }

        var provider = fixture.Factory.Services.GetRequiredService<IFieldCatalogProvider>();
        provider.Invalidate();

        var catalog = await provider.FindAsync(code);
        catalog!.Version.Should().Be("1.10.0");
        catalog.Fields.Select(f => f.Key).Should().Equal("primero", "segundo");
        catalog.Fields[0].Core.Should().BeTrue();
        (await provider.ListAsync()).Single(s => s.FormCode == code).Title.Should().Be("Diez");
    }

    [Fact]
    public async Task UnCatalogoMalFormadoOSinCamposSeOmiteSinTumbarLosDemas()
    {
        var broken = $"broken-{Guid.NewGuid():N}"[..20];
        var empty = $"empty-{Guid.NewGuid():N}"[..20];
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var brokenCatalog = FormCatalog.Create(broken, "1.0.0", "Roto", "[]");
            db.FormCatalogs.AddRange(brokenCatalog, FormCatalog.Create(empty, "1.0.0", "Vacío", "[]"));
            // jsonb válido pero "editable" no es un arreglo de textos.
            db.FormCatalogFields.Add(FormCatalogField.Create(
                brokenCatalog.Id, "campo", 1, "text",
                editable: "\"no es un arreglo\"", defaults: "{}", binding: "{}"));
            await db.SaveChangesAsync();
        }

        var provider = fixture.Factory.Services.GetRequiredService<IFieldCatalogProvider>();
        provider.Invalidate();

        (await provider.FindAsync(broken)).Should().BeNull();
        (await provider.FindAsync(empty)).Should().BeNull("un catálogo sin campos activos no se ofrece");
        (await provider.FindAsync(Code)).Should().NotBeNull();
    }

    //#endregion

    //#region Resolve

    [Fact]
    public async Task Resolve_sinFormularioDeLaOrganizacion_devuelveElFormularioBaseGlobal()
    {
        var response = await ClientFor(Guid.NewGuid()).GetAsync($"/api/forms/resolve/{Code}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var form = await ReadAsync(response);
        form.OrganizationId.Should().BeNull();
        form.Version.Should().Be("1.0.0");
    }

    [Fact]
    public async Task Resolve_codigoInexistente_da404()
    {
        (await ClientFor(Guid.NewGuid()).GetAsync("/api/forms/resolve/no-existe")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Resolve_prefiereLaVersionPublicadaMasRecienteDeLaOrganizacion()
    {
        var organization = Guid.NewGuid();
        var client = ClientFor(organization, "org_admin");
        var document = await GlobalPresetDocumentAsync(client);

        // 1.9.0 y 1.10.0 publicadas (1.10.0 es mayor aunque como texto sea menor) y 2.0.0 sin publicar.
        foreach (var version in new[] { "1.9.0", "1.10.0" })
        {
            var created = await ReadAsync(await CreateAsync(client, version, document));
            (await client.PostAsync($"/api/forms/{created.Id}/publish", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        (await CreateAsync(client, "2.0.0", document)).StatusCode.Should().Be(HttpStatusCode.Created);

        var resolved = await ReadAsync(await client.GetAsync($"/api/forms/resolve/{Code}"));
        resolved.Version.Should().Be("1.10.0");
        resolved.OrganizationId.Should().Be(organization);

        // Otra organización sigue viendo el formulario base global.
        var other = await ReadAsync(await ClientFor(Guid.NewGuid()).GetAsync($"/api/forms/resolve/{Code}"));
        other.OrganizationId.Should().BeNull();
    }

    //#endregion

    //#region Documentos del studio

    [Fact]
    public async Task CreateForm_deUnDocumentoDelStudio_generaElMapeoEIgnoraElDelCliente()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var document = await GlobalPresetDocumentAsync(client);

        var response = await CreateAsync(client, "1.1.0", document);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var mapping = (await ReadAsync(response)).FhirMappingJson;
        ItemCount(mapping).Should().Be(6);
        mapping.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("linkId").GetString())
            .Should().Contain(["identifierType", "birthDate"]);
    }

    [Fact]
    public async Task CreateForm_deUnDocumentoInvalido_devuelve400ConElMotivo()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var invalid = JsonDocument.Parse("""
            {"kind":"studio-v1","sections":[{"id":"s","label":"S","rows":[[{"field":"campo-que-no-existe"}]]}]}
            """).RootElement;

        var response = await CreateAsync(client, "1.1.0", invalid);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("campo-que-no-existe");
    }

    [Fact]
    public async Task CreateForm_sinCatalogoParaElTipo_devuelve400()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var document = await GlobalPresetDocumentAsync(client);

        var response = await client.PostAsJsonAsync("/api/forms", new
        {
            code = "tipo-sin-catalogo",
            title = "Sin catálogo",
            version = "1.0.0",
            schemaJson = document,
            fhirMappingJson = JsonDocument.Parse("""{"items":[]}""").RootElement,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("No hay catálogo");
    }

    [Fact]
    public async Task UpdateForm_conUnDocumentoInvalido_devuelve400YNoCambiaElBorrador()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var document = await GlobalPresetDocumentAsync(client);
        var created = await ReadAsync(await CreateAsync(client, "1.1.0", document));

        var invalid = JsonDocument.Parse("""{"kind":"studio-v1","sections":[]}""").RootElement;
        var update = await client.PutAsJsonAsync($"/api/forms/{created.Id}", new
        {
            title = "Otro", schemaJson = invalid, fhirMappingJson = JsonDocument.Parse("{}").RootElement,
        });

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var stored = await ReadAsync(await client.GetAsync($"/api/forms/{created.Id}"));
        stored.Title.Should().Be("Registro");
        ItemCount(stored.FhirMappingJson).Should().Be(6);
    }

    [Fact]
    public async Task PublishForm_regeneraElMapeoDelDocumento()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var document = await GlobalPresetDocumentAsync(client);
        var created = await ReadAsync(await CreateAsync(client, "1.1.0", document));

        // El borrador queda con un mapeo desactualizado (simula un catálogo que evolucionó).
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stale = await db.FormDefinitions.SingleAsync(x => x.Id == created.Id);
            stale.EditDraft(stale.Title, stale.SchemaJson, """{"version":"1.0","items":[]}""", stale.Description, stale.SpecialtyCode);
            await db.SaveChangesAsync();
        }

        var published = await ReadAsync(await client.PostAsync($"/api/forms/{created.Id}/publish", content: null));

        published.Status.Should().Be("published");
        ItemCount(published.FhirMappingJson).Should().Be(6);
    }

    [Fact]
    public async Task UnFormularioDeEncuentroSeGuardaYSePublicaSinCambios()
    {
        var client = ClientFor(Guid.NewGuid(), "org_admin");
        var schema = JsonDocument.Parse("""{"version":"1.0","type":"form","items":[]}""").RootElement;
        var mapping = JsonDocument.Parse("""{"version":"1.0","items":[{"linkId":"plan"}]}""").RootElement;

        var created = await ReadAsync(await client.PostAsJsonAsync("/api/forms", new
        {
            code = "test-encuentro", title = "Encuentro", version = "1.0.0", schemaJson = schema, fhirMappingJson = mapping,
        }));
        var published = await ReadAsync(await client.PostAsync($"/api/forms/{created.Id}/publish", content: null));

        ItemCount(published.FhirMappingJson).Should().Be(1);
        published.FhirMappingJson.GetProperty("items")[0].GetProperty("linkId").GetString().Should().Be("plan");
    }

    [Fact]
    public async Task ElFormularioBaseDelStudioNoAfectaALosFormulariosDeEncuentro()
    {
        await Task.Delay(500); // el sembrador de presets de encuentro corre al arrancar

        var response = await ClientFor(Guid.NewGuid()).GetAsync("/api/forms/by-specialty/general");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(response)).Code.Should().Be("general");
    }

    //#endregion

    private sealed record FormResponse(
        Guid Id,
        string Code,
        string Version,
        string Status,
        string Title,
        JsonElement SchemaJson,
        JsonElement FhirMappingJson,
        Guid? OrganizationId);
}
