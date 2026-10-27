using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Authorization;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.FhirGateway;

/// <summary>
/// Lo que el cierre de consulta necesita escribir por el proxy para los antecedentes (Épica P-1):
/// la <c>List</c> de cada sección y la <c>Provenance</c> del cierre.
///
/// <para>La <c>Provenance</c> es la delicada. El laboratorio la usa como cadena de custodia, y por
/// eso era un recurso que nunca se escribía por acá. La excepción es angosta —solo alta, solo sobre
/// antecedentes— y estas pruebas son las que la mantienen angosta.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class HistoryWriteAccessTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly Guid Org = Guid.NewGuid();

    private async Task<FhirAccessDecision> DecideAsync(string method, string url, string? body, params string[] roles)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var evaluator = scope.ServiceProvider.GetRequiredService<FhirAccessEvaluator>();
        var permissions = await scope.ServiceProvider.GetRequiredService<IPermissionResolver>().ResolveAsync(Org, roles);
        var descriptor = FhirRequestParser.Parse(method, url);
        descriptor.Should().NotBeNull();
        return await evaluator.EvaluateAsync(new FhirCallerContext(Org, Guid.NewGuid(), permissions, null), descriptor!, body);
    }

    private static string Provenance(params string[] targets) =>
        "{\"resourceType\":\"Provenance\",\"recorded\":\"2026-10-04T15:00:00Z\",\"target\":["
        + string.Join(",", targets.Select(t => $"{{\"reference\":\"{t}\"}}"))
        + "],\"agent\":[{\"who\":{\"reference\":\"Practitioner/dr-1\"}}]}";

    // ---- Provenance --------------------------------------------------------------------------

    [Fact]
    public async Task LaConstanciaDelCierre_sobreAntecedentes_pasa()
    {
        var decision = await DecideAsync("POST", "Provenance",
            Provenance("AllergyIntolerance/a1", "List/l1", "urn:uuid:3f1c0a52-0000-0000-0000-000000000001"),
            "practitioner");

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task LaConstanciaDelCierre_firmaProblemasYMedicacion()
    {
        var decision = await DecideAsync("POST", "Provenance",
            Provenance("Condition/c1", "MedicationStatement/m1", "List/l1"), "practitioner");

        decision.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task LaMedicacionHabitual_seLeeYSeEscribePorPaciente()
    {
        var search = await DecideAsync("GET", "MedicationStatement?subject=Patient/p1&status=recorded,draft", null, "practitioner");
        var write = await DecideAsync("PUT", "MedicationStatement/m1", "{\"resourceType\":\"MedicationStatement\"}", "practitioner");
        var all = await DecideAsync("GET", "MedicationStatement?status=recorded", null, "practitioner");

        search.Allowed.Should().BeTrue();
        search.Intent.Should().Be("patient.medicationStatements");
        write.Allowed.Should().BeTrue();
        all.Allowed.Should().BeFalse("la medicación de toda la organización no es una forma declarada");
    }

    [Fact]
    public async Task LaConstanciaDelCierre_firmaElRestoDeLosAntecedentes()
    {
        var decision = await DecideAsync("POST", "Provenance",
            Provenance("Procedure/p1", "FamilyMemberHistory/f1", "Immunization/i1", "DeviceUsage/d1"), "practitioner");

        decision.Allowed.Should().BeTrue();
    }

    [Theory]
    [InlineData("Procedure?subject=Patient/p1", "patient.procedures")]
    [InlineData("FamilyMemberHistory?patient=Patient/p1", "patient.familyHistory")]
    [InlineData("Immunization?patient=Patient/p1", "patient.immunizations")]
    [InlineData("DeviceUsage?patient=Patient/p1&status=active", "patient.devices")]
    [InlineData("Observation?subject=Patient/p1&category=social-history&_sort=-date", "patient.historyObservations")]
    public async Task LosAntecedentes_seLeenPorPaciente(string url, string intent)
    {
        var decision = await DecideAsync("GET", url, null, "practitioner");

        decision.Allowed.Should().BeTrue();
        decision.Intent.Should().Be(intent);
    }

    [Theory]
    [InlineData("Procedure?status=completed")]
    [InlineData("Immunization")]
    [InlineData("Observation?subject=Patient/p1&category=laboratory")]
    public async Task SinPacienteOConOtraCategoria_noEsUnaFormaDeclarada(string url)
    {
        // La última es la importante: la forma de antecedentes no puede ser una puerta trasera a
        // los resultados de laboratorio.
        var decision = await DecideAsync("GET", url, null, "practitioner");

        decision.Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("DiagnosticReport/dr-1")]
    [InlineData("Observation/obs-1")]
    [InlineData("Specimen/sp-1")]
    public async Task UnaProvenanceSobreElCircuitoDeLaboratorio_sigueCerrada(string target)
    {
        // Sería una firma de validación fabricada sobre un resultado.
        var decision = await DecideAsync("POST", "Provenance", Provenance("AllergyIntolerance/a1", target),
            "org_admin", "lab_supervisor");

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("endpoints de negocio");
    }

    [Fact]
    public async Task UnaProvenanceNoSeCorrige()
    {
        var decision = await DecideAsync("PUT", "Provenance/p1", Provenance("AllergyIntolerance/a1"), "org_admin");

        decision.Allowed.Should().BeFalse("una constancia que estuvo mal se reemplaza con otra, no se reescribe");
    }

    [Fact]
    public async Task UnaProvenanceSinDestino_noPasa()
    {
        var decision = await DecideAsync("POST", "Provenance", Provenance(), "practitioner");

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task UnBundleDeCierre_conProvenanceSobreUnResultado_seNiegaEntero()
    {
        var bundle = """
        {"resourceType":"Bundle","type":"transaction","entry":[
          {"fullUrl":"urn:uuid:a","resource":{"resourceType":"AllergyIntolerance","patient":{"reference":"Patient/p1"}},
           "request":{"method":"POST","url":"AllergyIntolerance"}},
          {"fullUrl":"urn:uuid:b","resource":{"resourceType":"Provenance","target":[{"reference":"urn:uuid:a"},{"reference":"DiagnosticReport/dr-1"}]},
           "request":{"method":"POST","url":"Provenance"}}
        ]}
        """;

        var decision = await DecideAsync("POST", "", bundle, "practitioner");

        decision.Allowed.Should().BeFalse();
    }

    [Fact]
    public async Task LasConstanciasSeLeenPorEncuentro_noDeTodaLaClinica()
    {
        var byEncounter = await DecideAsync("GET", "Provenance?encounter=Encounter/e1", null, "practitioner");
        var all = await DecideAsync("GET", "Provenance?_count=50", null, "practitioner");

        byEncounter.Allowed.Should().BeTrue();
        byEncounter.Intent.Should().Be("encounter.provenance");
        all.Allowed.Should().BeFalse();
    }

    // ---- List --------------------------------------------------------------------------------

    [Fact]
    public async Task ElMedico_escribeLaListaDeUnaSeccion()
    {
        var create = await DecideAsync("POST", "List", "{\"resourceType\":\"List\"}", "practitioner");
        var update = await DecideAsync("PUT", "List/l1", "{\"resourceType\":\"List\",\"id\":\"l1\"}", "practitioner");

        create.Allowed.Should().BeTrue();
        update.Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task LasListasSeBuscanPorPaciente()
    {
        var decision = await DecideAsync("GET", "List?subject=Patient/p1&code=http://loinc.org|48765-2", null, "practitioner");

        decision.Allowed.Should().BeTrue();
        decision.Intent.Should().Be("patient.historyLists");
    }

    [Fact]
    public async Task LasListasDeTodaLaOrganizacion_noSonUnaFormaDeclarada()
    {
        var decision = await DecideAsync("GET", "List?code=http://loinc.org|48765-2", null, "practitioner");

        decision.Allowed.Should().BeFalse();
        decision.Stage.Should().Be(FhirDecisionStage.Intent);
    }

    [Fact]
    public async Task LaAuxiliar_noLeeListasDeAntecedentes()
    {
        var decision = await DecideAsync("GET", "List?subject=Patient/p1", null, "auxiliar");

        decision.Allowed.Should().BeFalse("su puerta a los antecedentes es la preclínica, acotada a la cita");
    }
}
