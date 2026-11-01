using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Forms.Studio;
using Xunit;

namespace IMed.Api.IntegrationTests.Forms.Studio;

/// <summary>El documento en árbol (studio-v2): validación y lectura de los documentos de secciones y filas.</summary>
public sealed class StudioTreeTests
{
    private const string ExtensionBase = "https://imed.example/fhir/StructureDefinition/patient-custom-field";

    private static FieldCatalog Catalog => StudioDocumentTests.Fixture.Catalog;

    private const string CoreFields =
        """{"type":"field","field":"identifierType"},{"type":"field","field":"identifierValue"},{"type":"field","field":"firstName"},{"type":"field","field":"lastName"},{"type":"field","field":"gender"},{"type":"field","field":"birthDate"}""";

    /// <summary>Árbol con los campos núcleo sueltos más los elementos dados.</summary>
    private static JsonObject Tree(string extra = "") =>
        JsonNode.Parse($$"""{"kind":"studio-v2","title":"Formulario","children":[{{CoreFields}}{{(extra.Length > 0 ? "," + extra : "")}}]}""")!.AsObject();

    private static IReadOnlyList<string> Validate(JsonNode doc) => StudioDocumentValidator.Validate(doc, Catalog);

    private const string Nested = """
        {"type":"tabs","id":"principal","tabs":[
          {"id":"datos","label":"Datos","children":[
            {"type":"container","id":"identidad","layout":"grid","columns":3,"children":[{"type":"field","field":"middleName"}]},
            {"type":"accordion","id":"mas","allowMultiple":true,"sections":[
              {"id":"nombres","title":"Nombres","expanded":true,"children":[
                {"type":"fieldset","id":"persona","legend":"Persona","columns":2,"collapsible":true,"children":[{"type":"field","field":"email"}]}
              ]}
            ]}
          ]}
        ]}
        """;

    [Fact]
    public void AceptaCamposSueltosYCajasAnidadas()
    {
        Validate(Tree()).Should().BeEmpty();
        Validate(Tree(Nested)).Should().BeEmpty();
    }

    [Fact]
    public void ElMapeoEsElMismoSinImportarDondeEstenLosCampos()
    {
        var flat = Tree("""{"type":"field","field":"middleName"},{"type":"field","field":"email"}""");
        var nested = Tree(Nested);

        string[] Linked(JsonObject doc) => JsonNode.Parse(StudioMappingBuilder.Build(doc, Catalog, ExtensionBase))!["items"]!
            .AsArray().Select(item => item!["linkId"]!.GetValue<string>()).Order().ToArray();

        Linked(nested).Should().Equal(Linked(flat));
    }

    [Fact]
    public void LeeUnDocumentoDeSeccionesYFilasComoElMismoFormulario()
    {
        var old = JsonNode.Parse(StudioDocumentTests.Fixture.DocumentJson())!.AsObject();
        var upgraded = StudioTree.ToTree(old);

        Validate(upgraded).Should().BeEmpty();
        StudioMappingBuilder.Build(upgraded, Catalog, ExtensionBase).Should().Be(StudioMappingBuilder.Build(old, Catalog, ExtensionBase));

        var tabs = upgraded["children"]![0]!;
        tabs["type"]!.GetValue<string>().Should().Be("tabs");
        tabs["tabs"]![0]!["children"]!.AsArray().Select(row => row!["columns"]!.GetValue<int>()).Should().Equal(2, 2, 2);
    }

    [Fact]
    public void RechazaUnDocumentoSinElementos()
    {
        Validate(JsonNode.Parse("""{"kind":"studio-v2","children":[]}""")!).Should().Equal("The form needs at least one element.");
    }

    [Fact]
    public void RechazaUnTipoQueNoSeOfreceYPropiedadesFueraDelContrato()
    {
        Validate(Tree("""{"type":"button","id":"x"}""")).Should().Contain("Unknown element type 'button'.");
        Validate(Tree("""{"type":"container","id":"caja","script":"alert(1)","children":[]}""")).Should().Contain("Unknown property 'script' in element 'caja'.");
        Validate(Tree("""{"field":"email"}""")).Should().Contain("Unknown element type ''.");
    }

    [Fact]
    public void RechazaIdsInvalidosYRepetidos()
    {
        var errors = Validate(Tree(
            """{"type":"container","id":"caja","children":[]},{"type":"container","id":"caja","children":[]},{"type":"tabs","id":"Bad Id","tabs":[{"id":"caja","label":"T","children":[]}]}"""));

        errors.Should().Contain("Element id 'caja' is repeated.");
        errors.Should().Contain("Element id 'Bad Id' is not valid.");
    }

    [Fact]
    public void RechazaCajasMasProfundasQueElLimite()
    {
        var inner = """{"type":"field","field":"email"}""";
        for (var level = StudioTreeValidator.MaxDepth + 1; level >= 1; level--)
        {
            inner = $$"""{"type":"container","id":"nivel{{level}}","children":[{{inner}}]}""";
        }

        Validate(Tree(inner)).Should().Contain($"Elements are nested too deep (at most {StudioTreeValidator.MaxDepth} levels).");
    }

    [Fact]
    public void RechazaUnLayoutFueraDeRango()
    {
        var errors = Validate(Tree(
            """{"type":"container","id":"a","columns":7,"children":[]},{"type":"container","id":"b","layout":"free","children":[]},{"type":"container","id":"c","gap":-1,"children":[]}"""));

        errors.Should().Contain($"'columns' on 'a' must be a whole number from 1 to {StudioTreeValidator.MaxColumns}.");
        errors.Should().Contain("'layout' on 'b' must be one of: grid, vertical.");
        errors.Should().Contain($"'gap' on 'c' must be a whole number from 0 to {StudioTreeValidator.MaxGap}.");
    }

    [Fact]
    public void RechazaUnFieldsetSinLeyendaYPestanasSinItems()
    {
        var errors = Validate(Tree("""{"type":"fieldset","id":"f","legend":" ","children":[]},{"type":"tabs","id":"t","tabs":[]}"""));

        errors.Should().Contain(e => e.Contains("'f' needs a legend"));
        errors.Should().Contain(e => e.Contains("'t' needs between 1 and"));
    }

    [Fact]
    public void RechazaUnCampoRepetidoFueraDelCatalogoOSinLosNucleo()
    {
        Validate(Tree("""{"type":"field","field":"email"},{"type":"container","id":"c","children":[{"type":"field","field":"email"}]}"""))
            .Should().Contain("Field 'email' is used more than once.");
        Validate(Tree("""{"type":"field","field":"nope"}""")).Should().Contain("Field 'nope' is not in the catalog.");

        var withoutGender = JsonNode.Parse("""{"kind":"studio-v2","children":[{"type":"field","field":"firstName"}]}""")!;
        Validate(withoutGender).Should().Contain("Core field 'gender' cannot be removed.");
    }

    [Fact]
    public void RechazaUnAnchoQueNoEsUnNumeroEnteroDeColumnas()
    {
        var errors = Validate(Tree("""{"type":"field","field":"email","span":13},{"type":"field","field":"phoneNumber","span":2.5}"""));

        errors.Should().Contain($"'span' on 'email' must be a whole number from 1 to {StudioTreeValidator.MaxSpan}.");
        errors.Should().Contain($"'span' on 'phoneNumber' must be a whole number from 1 to {StudioTreeValidator.MaxSpan}.");
    }

    [Fact]
    public void RechazaDemasiadosElementosEnUnaCaja()
    {
        var many = string.Join(",", Enumerable.Range(0, StudioTreeValidator.MaxChildren + 1).Select(i => $$"""{"type":"container","id":"c{{i}}","children":[]}"""));

        Validate(Tree(many)).Should().Contain("the form has too many elements.");
    }
}
