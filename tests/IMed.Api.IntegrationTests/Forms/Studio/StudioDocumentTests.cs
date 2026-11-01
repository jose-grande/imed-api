using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Forms.Studio;
using Xunit;

namespace IMed.Api.IntegrationTests.Forms.Studio;

/// <summary>
/// Pruebas del validador y del generador de mapeo del studio de formularios, con un catálogo pequeño
/// escrito aquí mismo. No necesitan base de datos ni host: la lógica es pura.
/// </summary>
public sealed class StudioDocumentTests
{
    internal const string Code = "studio-test";
    private const string ExtensionBase = "https://example.org/fhir/StructureDefinition/patient-custom-field";

    /// <summary>Catálogo de prueba: seis campos núcleo, cinco opcionales y dos plantillas personalizadas.</summary>
    internal static class Fixture
    {
        internal sealed record Def(string Key, string Widget, bool Core, bool Custom, string Editable, string Binding);

        private const string Name = """{"mapsTo":"Patient.name","extractor":"human-name","part":"given"}""";

        internal static readonly Def[] Fields =
        [
            new("identifierType", "select", true, false, """["label"]""", """{"mapsTo":"Patient.identifier","extractor":"identifier","part":"type"}"""),
            new("identifierValue", "text", true, false, """["label"]""", """{"mapsTo":"Patient.identifier","extractor":"identifier","part":"value"}"""),
            new("firstName", "text", true, false, """["label"]""", Name),
            new("lastName", "text", true, false, """["label"]""", """{"mapsTo":"Patient.name","extractor":"human-name","part":"family"}"""),
            new("gender", "select", true, false, """["label"]""", """{"mapsTo":"Patient.gender","extractor":"primitive"}"""),
            new("birthDate", "date", true, false, """["label"]""", """{"mapsTo":"Patient.birthDate","extractor":"primitive"}"""),
            new("middleName", "text", false, false, """["label","required"]""", Name),
            new("secondLastName", "text", false, false, """["label"]""", """{"mapsTo":"Patient.name","extractor":"human-name","part":"family"}"""),
            new("email", "text", false, false, """["label","helpText","required"]""", """{"mapsTo":"Patient.telecom","extractor":"contact-point","system":"email"}"""),
            new("phoneNumber", "text", false, false, """["label"]""", """{"mapsTo":"Patient.telecom","extractor":"contact-point","system":"phone"}"""),
            new("prefix", "text", false, false, """["label"]""", """{"mapsTo":"Patient.name","extractor":"human-name","part":"prefix"}"""),
            new("custom_text", "text", false, true, """["label","placeholder","helpText","required"]""", """{"mapsTo":"Patient.extension","extractor":"extension","valueType":"string"}"""),
            new("custom_toggle", "toggle", false, true, """["label","helpText","onLabel","offLabel"]""", """{"mapsTo":"Patient.extension","extractor":"extension","valueType":"boolean"}"""),
        ];

        internal static FieldCatalog Catalog => new(
            Code,
            "1.0.0",
            Fields.Select(d => new CatalogField(
                d.Key,
                d.Core,
                d.Custom,
                JsonSerializer.Deserialize<string[]>(d.Editable)!,
                JsonNode.Parse(d.Binding)!.AsObject())).ToList());

        /// <summary>Las filas con los seis campos núcleo, que todo documento válido necesita.</summary>
        internal const string CoreRows =
            """[{"field":"identifierType"},{"field":"identifierValue"}],[{"field":"firstName"},{"field":"lastName"}],[{"field":"gender"},{"field":"birthDate"}]""";

        /// <summary>Documento válido con solo los campos núcleo.</summary>
        internal static string DocumentJson(string title = "Formulario de prueba") =>
            $$"""{"kind":"studio-v1","title":"{{title}}","sections":[{"id":"unica","label":"Única","rows":[{{CoreRows}}]}]}""";
    }

    /// <summary>Proveedor en memoria con el catálogo de prueba.</summary>
    private sealed class FakeCatalogProvider : IFieldCatalogProvider
    {
        public Task<IReadOnlyList<FormCatalogSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FormCatalogSummary>>([new FormCatalogSummary(Code, "1.0.0", "Prueba", [])]);

        public Task<FieldCatalog?> FindAsync(string formCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(formCode == Code ? Fixture.Catalog : null);

        public Task<string?> GetCatalogJsonAsync(string formCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public void Invalidate()
        {
        }
    }

    private static FieldCatalog Catalog => Fixture.Catalog;

    private static JsonObject BaseDocument() => JsonNode.Parse(Fixture.DocumentJson())!.AsObject();

    /// <summary>Documento con una sola sección y las filas dadas (cada fila es un arreglo JSON de campos).</summary>
    private static JsonObject Doc(params string[] rows) =>
        JsonNode.Parse($$"""{"kind":"studio-v1","sections":[{"id":"unica","label":"Única","rows":[{{string.Join(",", rows)}}]}]}""")!.AsObject();

    private static IReadOnlyList<string> Validate(JsonNode? doc) => StudioDocumentValidator.Validate(doc, Catalog);

    [Fact]
    public void ElDocumentoConSoloLosCamposNucleoEsValido()
    {
        Validate(BaseDocument()).Should().BeEmpty();
    }

    [Fact]
    public void AceptaCamposOpcionalesYPersonalizadosConSusAjustes()
    {
        var doc = Doc(Fixture.CoreRows,
            """[{"field":"middleName","overrides":{"label":"Otro nombre","required":true}},{"field":"email","overrides":{"helpText":"Ayuda"}}]""",
            """[{"field":"custom_text","id":"custom_seguro","overrides":{"label":"Seguro"}}]""");

        Validate(doc).Should().BeEmpty();
    }

    [Fact]
    public void ValidaLosTextosDeActivadoYDesactivadoDeUnToggle()
    {
        var valido = Doc(Fixture.CoreRows, """[{"field":"email","overrides":{"onLabel":"Sí"}}]""",
            """[{"field":"custom_toggle","id":"custom_donante","overrides":{"onLabel":"Sí","offLabel":"No"}}]""");
        var errors = Validate(valido);
        errors.Should().Contain("'onLabel' cannot be changed on field 'email'.");
        errors.Should().NotContain(e => e.Contains("custom_donante"));

        var largo = Doc(Fixture.CoreRows,
            $$$"""[{"field":"custom_toggle","id":"custom_donante","overrides":{"offLabel":"{{{new string('x', 201)}}}"}}]""");
        Validate(largo).Should().Contain(e => e.Contains("'offLabel'") && e.Contains("200 characters"));
    }

    [Fact]
    public void RechazaUnDocumentoConOtroKindOSinSecciones()
    {
        Validate(JsonNode.Parse("""{"kind":"otro","sections":[]}""")).Should().Equal("kind must be 'studio-v1' or 'studio-v2'.");
        Validate(JsonNode.Parse("""{"kind":"studio-v1","sections":[]}""")).Should().Equal("The form needs at least one section.");
        Validate(null).Should().Equal("kind must be 'studio-v1' or 'studio-v2'.");
    }

    [Fact]
    public void RechazaUnCampoFueraDelCatalogo()
    {
        var errors = Validate(Doc(Fixture.CoreRows, """[{"field":"bloodType"}]"""));
        errors.Should().Contain("Field 'bloodType' is not in the catalog.");
    }

    [Fact]
    public void RechazaQuitarUnCampoNucleo()
    {
        var errors = Validate(Doc("""[{"field":"identifierType"},{"field":"identifierValue"}],[{"field":"firstName"},{"field":"lastName"}]"""));
        errors.Should().Contain("Core field 'gender' cannot be removed.");
        errors.Should().Contain("Core field 'birthDate' cannot be removed.");
    }

    [Fact]
    public void RechazaCambiosQueElCampoNoPermite()
    {
        var errors = Validate(Doc(
            """[{"field":"identifierType"},{"field":"identifierValue"}],[{"field":"firstName","overrides":{"required":false}},{"field":"lastName"}],[{"field":"gender"},{"field":"birthDate"}]"""));
        errors.Should().Contain("'required' cannot be changed on field 'firstName'.");
    }

    [Fact]
    public void RechazaUnCampoRepetido()
    {
        Validate(Doc(Fixture.CoreRows, """[{"field":"firstName"}]""")).Should().Contain("Field 'firstName' is used more than once.");
    }

    [Fact]
    public void RechazaIdEnCampoNoPersonalizadoYCustomSinIdValido()
    {
        Validate(Doc(Fixture.CoreRows, """[{"field":"email","id":"custom_x"}]"""))
            .Should().Contain("Field 'email' is not custom and cannot have an id.");
        Validate(Doc(Fixture.CoreRows, """[{"field":"custom_text","id":"Seguro Privado"}]"""))
            .Should().Contain("Custom field id 'Seguro Privado' must look like custom_name.");
    }

    [Fact]
    public void RechazaPropiedadesFueraDelContrato()
    {
        var errors = Validate(Doc(
            """[{"field":"identifierType","onChange":"alert(1)"},{"field":"identifierValue"}],[{"field":"firstName"},{"field":"lastName"}],[{"field":"gender"},{"field":"birthDate"}]"""));
        errors.Should().Contain("Unknown property 'onChange' in field 'identifierType'.");

        var doc = Doc(Fixture.CoreRows);
        doc["sections"]![0]!["script"] = "x";
        Validate(doc).Should().Contain("Unknown property 'script' in section 'unica'.");
    }

    [Fact]
    public void RechazaTextosVaciosOExcedidos()
    {
        var tooLong = new string('x', 201);
        var errors = Validate(Doc(
            "[{\"field\":\"identifierType\"},{\"field\":\"identifierValue\"}]," +
            "[{\"field\":\"middleName\",\"overrides\":{\"label\":\"   \"}},{\"field\":\"secondLastName\",\"overrides\":{\"label\":\"" + tooLong + "\"}}]," +
            "[{\"field\":\"firstName\"},{\"field\":\"lastName\"}],[{\"field\":\"gender\"},{\"field\":\"birthDate\"}]"));

        errors.Should().Contain(e => e.StartsWith("'label' on 'middleName'", StringComparison.Ordinal));
        errors.Should().Contain(e => e.StartsWith("'label' on 'secondLastName'", StringComparison.Ordinal));
    }

    [Fact]
    public void RechazaFilasConDemasiadosCampos()
    {
        var errors = Validate(Doc(Fixture.CoreRows,
            """[{"field":"phoneNumber"},{"field":"email"},{"field":"prefix"},{"field":"middleName"},{"field":"secondLastName"}]"""));
        errors.Should().Contain(e => e.StartsWith("A row in", StringComparison.Ordinal));
    }

    [Fact]
    public void ElMapeoTraeUnItemPorCampoConSuBindingCopiado()
    {
        var doc = Doc(Fixture.CoreRows, """[{"field":"email"}]""");

        var mapping = JsonNode.Parse(StudioMappingBuilder.Build(doc, Catalog, ExtensionBase))!.AsObject();
        var items = mapping["items"]!.AsArray().Select(i => i!.AsObject()).ToList();

        mapping["version"]!.GetValue<string>().Should().Be("1.0");
        items.Should().HaveCount(7);
        items.Select(i => i["linkId"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();

        var birthDate = items.Single(i => i["linkId"]!.GetValue<string>() == "birthDate");
        birthDate["mapsTo"]!.GetValue<string>().Should().Be("Patient.birthDate");
        birthDate["extractor"]!.GetValue<string>().Should().Be("primitive");

        var email = items.Single(i => i["linkId"]!.GetValue<string>() == "email");
        email["mapsTo"]!.GetValue<string>().Should().Be("Patient.telecom");
        email["system"]!.GetValue<string>().Should().Be("email");
    }

    [Fact]
    public void LosCamposPersonalizadosGeneranUnaExtensionConSuUrl()
    {
        var doc = Doc(Fixture.CoreRows,
            """[{"field":"custom_text","id":"custom_seguro_secundario","overrides":{"label":"Seguro secundario"}},{"field":"custom_toggle","id":"custom_donante"}]""");

        Validate(doc).Should().BeEmpty();

        var mapping = JsonNode.Parse(StudioMappingBuilder.Build(doc, Catalog, ExtensionBase + "/"))!.AsObject();
        var items = mapping["items"]!.AsArray().Select(i => i!.AsObject()).ToList();
        var custom = items.Single(i => i["linkId"]!.GetValue<string>() == "custom_seguro_secundario");

        custom["mapsTo"]!.GetValue<string>().Should().Be("Patient.extension");
        custom["extractor"]!.GetValue<string>().Should().Be("extension");
        custom["valueType"]!.GetValue<string>().Should().Be("string");
        custom["extensionUrl"]!.GetValue<string>().Should().Be($"{ExtensionBase}/seguro-secundario");
        items.Single(i => i["linkId"]!.GetValue<string>() == "custom_donante")["valueType"]!
            .GetValue<string>().Should().Be("boolean");
    }

    [Fact]
    public async Task ElProcesadorNoTocaLosFormulariosQueNoSonDelStudio()
    {
        var studio = new StudioFormProcessor(new FakeCatalogProvider(), new FormStudioOptions());
        const string schema = """{"version":"1.0","type":"form","items":[]}""";
        const string mapping = """{"items":[{"linkId":"plan"}]}""";

        var result = await studio.PrepareAsync("general", schema, mapping);

        result.SchemaJson.Should().Be(schema);
        result.FhirMappingJson.Should().Be(mapping);
    }

    [Fact]
    public async Task ElProcesadorReemplazaElMapeoDeUnDocumentoDelStudio()
    {
        var studio = new StudioFormProcessor(new FakeCatalogProvider(), new FormStudioOptions { ExtensionBaseUrl = ExtensionBase });

        var result = await studio.PrepareAsync(Code, Fixture.DocumentJson("Registro"), """{"items":[]}""");

        JsonNode.Parse(result.FhirMappingJson)!["items"]!.AsArray().Should().HaveCount(6);
        result.DocumentTitle.Should().Be("Registro");
    }

    [Fact]
    public async Task ElProcesadorRechazaUnDocumentoInvalidoYUnFormularioSinCatalogo()
    {
        var studio = new StudioFormProcessor(new FakeCatalogProvider(), new FormStudioOptions());

        var invalid = () => studio.PrepareAsync(Code, Doc("""[{"field":"nope"}]""").ToJsonString(), "{}");
        await invalid.Should().ThrowAsync<ArgumentException>().WithMessage("*Field 'nope' is not in the catalog.*");

        var noCatalog = () => studio.PrepareAsync("otro-formulario", Fixture.DocumentJson(), "{}");
        await noCatalog.Should().ThrowAsync<ArgumentException>().WithMessage("*No hay catálogo*");
    }
}
