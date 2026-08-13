using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Authorization;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.FhirGateway;

/// <summary>
/// Motor de decisión del proxy FHIR (ADR-007, SEC-3).
///
/// <para>Es lo que el <c>AuthorizationInterceptor</c> nativo de HAPI regalaba en la alternativa
/// que se descartó: la evaluación de Bundles entrada por entrada y el acotado de búsquedas. Al
/// escribirlo nosotros, una equivocación acá se paga en silencio — de ahí que cada regla tenga
/// su prueba y que las de Bundle sean las más insistentes.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class FhirAccessEvaluatorTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid Org = Guid.NewGuid();

    private async Task<(FhirAccessEvaluator Evaluator, EffectivePermissions Permissions)> ArrangeAsync(
        params string[] roles)
    {
        var scope = fixture.Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<FhirAccessEvaluator>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var permissions = await resolver.ResolveAsync(Org, roles);
        return (evaluator, permissions);
    }

    private async Task<FhirAccessDecision> DecideAsync(
        string method, string url, string? body = null, params string[] roles)
    {
        var (evaluator, permissions) = await ArrangeAsync(roles);
        var descriptor = FhirRequestParser.Parse(method, url);
        descriptor.Should().NotBeNull($"la ruta '{url}' debe poder describirse");
        return await evaluator.EvaluateAsync(Org, permissions, descriptor!, body);
    }

    // ---------------------------------------------------------------- básicos

    [Fact]
    public async Task UnMedico_puedeLeerPacientes()
    {
        var decision = await DecideAsync("GET", "Patient/123", null, "practitioner");
        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnRecursoNoDeclarado_seNiega()
    {
        var decision = await DecideAsync("GET", "Media/123", null, "org_admin");

        decision.Allowed.Should().BeFalse("default deny: lo que no está en la lista blanca no pasa");
        decision.Reason.Should().Contain("Media");
    }

    [Fact]
    public async Task ElAuxiliar_noPuedeLeerResultados()
    {
        var decision = await DecideAsync("GET", "DiagnosticReport/abc", null, "auxiliar");
        decision.Allowed.Should().BeFalse();
    }

    // ------------------------------------------- invariantes no configurables

    [Theory]
    [InlineData("PUT", "DiagnosticReport/abc")]
    [InlineData("POST", "DiagnosticReport")]
    [InlineData("POST", "Provenance")]
    [InlineData("POST", "Specimen")]
    [InlineData("PUT", "ServiceRequest/sr-1")]
    public async Task LosRecursosConCircuitoDeNegocio_noSeEscribenPorAca(string method, string url)
    {
        // Ni siquiera con el rol más amplio: es un invariante, no una política configurable.
        var decision = await DecideAsync(method, url, null, "org_admin", "lab_supervisor");

        decision.Allowed.Should().BeFalse(
            "es el ataque que SEC-3 existe para cerrar: PUT status=final salteándose la validación");
        decision.Reason.Should().Contain("endpoints de negocio");
    }

    // ------------------------------- Observation: el invariante es la categoría

    /// <summary>
    /// Observation de signos vitales, tal como la manda preclínica.
    ///
    /// <para>Se arma con marcador y <c>Replace</c> en vez de interpolación: el JSON está lleno de
    /// llaves consecutivas y la cadena interpolada las reclama para sí.</para>
    /// </summary>
    private static string VitalSign(string code = "vital-signs") => """
    {"resourceType":"Observation","status":"final",
     "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/observation-category","code":"@CODE@"}]}],
     "code":{"coding":[{"system":"http://loinc.org","code":"29463-7"}]},
     "subject":{"reference":"Patient/p1"},
     "valueQuantity":{"value":70,"unit":"kg"}}
    """.Replace("@CODE@", code, StringComparison.Ordinal);

    [Fact]
    public async Task LosSignosVitales_seEscribenDesdeElCliente()
    {
        // Preclínica los captura a mano y no hay circuito de negocio que atravesar. Prohibirlos
        // era lo que hacía fallar la captura entera y, peor, el Bundle de cierre de consulta.
        var decision = await DecideAsync("POST", "Observation", VitalSign(), "auxiliar");

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnResultadoDeLaboratorio_noSeEscribeDesdeElCliente()
    {
        // Ni con el rol más amplio: es el circuito de laboratorio lo que se protege, y ese sigue
        // cerrado aunque Observation ya no esté prohibida como recurso.
        var decision = await DecideAsync(
            "POST", "Observation", VitalSign("laboratory"), "org_admin", "lab_supervisor");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("circuito de laboratorio");
    }

    [Fact]
    public async Task UnaObservationSinCategoria_seNiega()
    {
        const string body = """
        {"resourceType":"Observation","status":"final","subject":{"reference":"Patient/p1"}}
        """;

        var decision = await DecideAsync("POST", "Observation", body, "org_admin");

        decision.Allowed.Should().BeFalse(
            "el default-deny vale también para el cuerpo: sin categoría podría ser un resultado");
    }

    [Fact]
    public async Task UnaObservationSinCuerpo_seNiega()
    {
        // Si el cuerpo no llegó, la categoría no se puede mirar — y lo que no se puede evaluar
        // no se aprueba.
        var decision = await DecideAsync("PUT", "Observation/o1", null, "org_admin");

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnaObservationDeVitalesSinPermisoDeEscritura_seNiega()
    {
        // La categoría abre la puerta; el permiso sigue mandando. Un rol que no escribe expediente
        // no escribe signos vitales por más que la categoría sea la correcta.
        var decision = await DecideAsync("POST", "Observation", VitalSign(), "lab_tech");

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ElCierreDeConsulta_conSignosVitalesAdentro_pasa()
    {
        // Es el caso que la sombra encontró: pediatría y prenatal proyectan Observations dentro
        // del Bundle de cierre, y rechazarlas tumbaba la transacción entera —QR, diagnósticos,
        // encuentro y cita— y no solo el signo vital.
        var body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"PUT","url":"QuestionnaireResponse/qr-1"}},
          {"resource":@OBS@,"request":{"method":"POST","url":"Observation"}},
          {"request":{"method":"PUT","url":"Encounter/enc-1"}}
        ]}
        """.Replace("@OBS@", VitalSign(), StringComparison.Ordinal);

        var decision = await DecideAsync("POST", "", body, "org_admin");

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnResultadoEscondidoEnUnBundle_seRechazaIgual()
    {
        // El Bundle no puede ser la puerta de atrás de la categoría, igual que no lo es del
        // borrado: la entrada se evalúa con su propio cuerpo.
        var body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"PUT","url":"QuestionnaireResponse/qr-1"}},
          {"resource":@OBS@,"request":{"method":"POST","url":"Observation"}}
        ]}
        """.Replace("@OBS@", VitalSign("laboratory"), StringComparison.Ordinal);

        var decision = await DecideAsync("POST", "", body, "org_admin", "lab_supervisor");

        decision.Allowed.Should().BeFalse();
        decision.DeniedResource.Should().Be("Observation");
    }

    [Fact]
    public async Task UnaObservationEnUnBundleSinSuRecurso_seRechaza()
    {
        // La entrada declara que crea una Observation pero no adjunta el recurso: no hay
        // categoría que mirar, así que no pasa.
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"POST","url":"Observation"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task ElBorrado_noSeAbreNunca()
    {
        var decision = await DecideAsync("DELETE", "Patient/123", null, "org_admin");

        decision.Allowed.Should().BeFalse("un expediente clínico se corrige, no se borra");
    }

    [Theory]
    [InlineData("GET", "Patient/$expunge")]
    [InlineData("GET", "$reindex")]
    [InlineData("GET", "Patient/123/_history")]
    public async Task LasOperacionesPeligrosas_seRechazan(string method, string url)
    {
        var decision = await DecideAsync(method, url, null, "org_admin");
        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task LaExpansionDeValueSet_siEstaHabilitada()
    {
        var decision = await DecideAsync("GET", "ValueSet/$expand?url=x", null, "auxiliar");
        decision.Allowed.Should().BeTrue("es terminología, no PHI, y la usa el formulario de paciente");
    }

    // ----------------------------------------------------- acotado de búsqueda

    [Fact]
    public async Task UnCountDesmedido_seRechaza()
    {
        var decision = await DecideAsync("GET", "Patient?_count=100000", null, "org_admin");

        decision.Allowed.Should().BeFalse("una búsqueda sin tope es una exportación del expediente");
        decision.Reason.Should().Contain("_count");
    }

    [Fact]
    public async Task UnIncludeNoDeclarado_seRechaza()
    {
        var decision = await DecideAsync(
            "GET", "Encounter?_include=Encounter:appointment", null, "org_admin");

        decision.Allowed.Should().BeFalse(
            "el include arbitrario es por donde una búsqueda acotada devuelve lo que nadie autorizó");
    }

    [Fact]
    public async Task UnIncludeDeclarado_pasa()
    {
        var decision = await DecideAsync(
            "GET", "Encounter?_include=Encounter:patient", null, "org_admin");
        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task ElParametroHas_seRechaza()
    {
        var decision = await DecideAsync(
            "GET", "Patient?_has:Observation:subject:code=1234", null, "org_admin");

        decision.Allowed.Should().BeFalse("_has ensancha de formas que no se pueden declarar caso por caso");
    }

    // ------------------------------------------------------- Bundles, lo filoso

    [Fact]
    public async Task UnBundleLimpio_pasa()
    {
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"PUT","url":"QuestionnaireResponse/qr-1"}},
          {"request":{"method":"GET","url":"Appointment/appt-1"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");
        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task UnBundleConUnaEntradaProhibida_seRechazaEntero()
    {
        // La primera entrada es legítima; la segunda es el ataque. Aceptar la parte autorizada
        // dejaría la transacción a medias, que es peor que no haberla intentado.
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"PUT","url":"QuestionnaireResponse/qr-1"}},
          {"request":{"method":"PUT","url":"DiagnosticReport/dr-1"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("Bundle");
        decision.DeniedResource.Should().Be("DiagnosticReport");
    }

    [Fact]
    public async Task UnBundleConBorrado_seRechaza()
    {
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"DELETE","url":"Patient/123"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");
        decision.Allowed.Should().BeFalse("el Bundle no puede ser la puerta de atrás del borrado");
    }

    [Fact]
    public async Task UnBundleConBusquedaDesmedida_seRechaza()
    {
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"request":{"method":"GET","url":"Patient?_count=99999"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");
        decision.Allowed.Should().BeFalse("el acotado vale igual adentro del Bundle que afuera");
    }

    [Fact]
    public async Task UnBundleIlegible_seRechaza()
    {
        var decision = await DecideAsync("POST", "", "{no es json", "org_admin");

        decision.Allowed.Should().BeFalse(
            "lo que no se puede evaluar no pasa: dejarlo seguir sería confiar en que HAPI lo rechace");
    }

    [Fact]
    public async Task UnBundleSinRequestEnUnaEntrada_seRechaza()
    {
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"resource":{"resourceType":"Patient"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");
        decision.Allowed.Should().BeFalse("una entrada sin request no se puede clasificar");
    }

    [Fact]
    public async Task UnBundleAnidado_seRechaza()
    {
        // Con su recurso adjunto: la entrada trae un Bundle entero adentro. Antes esto se cortaba
        // solo porque la entrada no recibía cuerpo; ahora que sí lo recibe, lo que lo corta es el
        // límite de profundidad, y esta prueba es la que lo verifica.
        const string body = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"resource":{"resourceType":"Bundle","type":"transaction","entry":[
             {"request":{"method":"PUT","url":"DiagnosticReport/dr-1"}}
           ]},
           "request":{"method":"POST","url":""}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", body, "org_admin");
        decision.Allowed.Should().BeFalse("un Bundle dentro de otro no tiene uso legítimo acá");
    }

    // ------------------------------------------------------------ parser

    [Fact]
    public void PostConSearch_seClasificaComoBusqueda_yNoComoCreacion()
    {
        var descriptor = FhirRequestParser.Parse("POST", "Patient/_search?name=ana");

        descriptor.Should().NotBeNull();
        descriptor!.Verb.Should().Be(FhirVerb.Search,
            "tratarlo como creación abriría la búsqueda a quien solo puede crear");
        descriptor.ResourceId.Should().BeNull();
    }

    [Fact]
    public void PostALaRaiz_esUnBundle()
    {
        var descriptor = FhirRequestParser.Parse("POST", "/");

        descriptor.Should().NotBeNull();
        descriptor!.IsBundle.Should().BeTrue();
    }
}
