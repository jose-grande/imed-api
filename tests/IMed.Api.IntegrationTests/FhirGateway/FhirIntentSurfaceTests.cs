using FluentAssertions;
using IMed.Api.Application.FhirGateway;
using Xunit;

namespace IMed.Api.IntegrationTests.FhirGateway;

/// <summary>
/// La superficie real de consulta del cliente, fijada contra el catálogo de formas (SEC-4).
///
/// <para><b>Por qué existe.</b> La sombra solo mide lo que alguien usa. Media docena de pantallas
/// —sedes, roles del equipo, contactos del paciente— se visitan poco, y sus formas se declararon
/// leyendo código en vez de midiendo. Esperar a que aparezcan en el log deja el paso a
/// <c>Enforce</c> colgado de que a alguien se le ocurra abrir la pantalla correcta; y si una forma
/// declarada no coincide con la real, el primero en descubrirlo es el usuario, con un 403 al
/// guardar.</para>
///
/// <para>Cada caso es una consulta que el cliente arma hoy, transcrita del servicio que la emite.
/// Si alguien cambia la consulta sin declarar la forma, o aprieta el catálogo sin mirar quién lo
/// usa, esto falla acá y no en producción. No reemplaza a la sombra: la sombra mide lo que pasa de
/// verdad, incluida la consulta que ningún servicio de este repo emite.</para>
/// </summary>
public sealed class FhirIntentSurfaceTests
{
    // (verbo, url, forma esperada, servicio del cliente que la emite)
    [Theory]
    // ---- Directorio y administración: las que la sombra todavía no vio ----
    [InlineData("GET", "PractitionerRole?_count=20&_offset=0&_sort=-_lastUpdated", "team.roles")]
    [InlineData("GET", "PractitionerRole?identifier=http%3A%2F%2Fimed.local%7C7&_summary=count", "team.roles")]
    [InlineData("GET", "Location", "locations.list")]
    [InlineData("GET", "Location?identifier=http%3A%2F%2Fimed.local%7C3&_summary=count", "locations.list")]
    [InlineData("GET", "Location?name:contains=central", "locations.list")]
    [InlineData("GET", "Location?status=active", "locations.list")]
    [InlineData("GET", "Location?organization=Organization/org-1", "locations.list")]
    [InlineData("GET", "Location?partof=Location/loc-1", "locations.list")]
    [InlineData("GET", "Practitioner?name:contains=ana", "directory.practitioners")]
    [InlineData("GET", "Practitioner?active=true", "directory.practitioners")]
    [InlineData("GET", "Practitioner?identifier=http%3A%2F%2Fimed.local%7C9", "directory.practitioners")]
    // ---- Contactos del paciente ----
    [InlineData("GET", "RelatedPerson?patient=Patient/p1&active=true&_count=50", "patient.contacts")]
    // ---- Registro clínico ----
    [InlineData("GET", "Condition?subject=Patient/p1&_sort=-recorded-date&_count=20", "patient.conditions")]
    [InlineData("GET", "Condition?encounter=Encounter/e1&_count=100", "patient.conditions")]
    [InlineData("GET", "AllergyIntolerance?patient=Patient/p1&clinical-status=active&_count=100", "patient.allergies")]
    [InlineData("GET", "MedicationRequest?encounter=Encounter/e1&_count=50", "encounter.prescriptions")]
    [InlineData("GET", "MedicationRequest?patient=Patient/p1&status=active,completed,stopped&_sort=-authoredon&_count=100", "patient.prescriptions")]
    [InlineData("GET", "QuestionnaireResponse?encounter=Encounter/e1&_count=10", "encounter.forms")]
    // ---- Encuentros, signos y agenda ----
    [InlineData("GET", "Encounter?subject=Patient/p1&_sort=-date&_count=20", "patient.encounters")]
    [InlineData("GET", "Encounter?appointment=Appointment/a1&status=in-progress&_count=3", "encounter.byAppointment")]
    [InlineData("GET", "Observation?encounter=Encounter/e1&_count=100", "encounter.observations")]
    [InlineData("GET", "Observation?subject=Patient/p1&category=vital-signs&date=ge2026-08-01&_sort=-date&_count=50", "patient.vitals")]
    [InlineData("GET", "Appointment?patient=Patient/p1&_sort=-date&_count=50", "patient.appointments")]
    [InlineData("GET", "Appointment?status=booked&_count=100", "agenda.all")]
    public void UnaConsultaQueElClienteArmaHoy_coincideConSuFormaDeclarada(
        string method, string url, string formaEsperada)
    {
        var descriptor = FhirRequestParser.Parse(method, url);
        descriptor.Should().NotBeNull($"'{url}' tiene que poder describirse");

        var match = FhirIntentMatcher.Match(descriptor!, callerPractitionerId: null);

        match.Intent.Should().NotBeNull(
            $"esta consulta la emite el cliente hoy; sin forma declarada, en Enforce el usuario "
            + $"recibe un 403. El catálogo respondió: {match.Reason}");
        match.Intent!.Name.Should().Be(formaEsperada);
    }

    /// <summary>
    /// La agenda propia se distingue de la ajena por el <c>Practitioner</c> del token, no por el
    /// parámetro. Va aparte porque es el único caso cuya forma depende de quién pregunta.
    /// </summary>
    [Fact]
    public void LaAgendaPropia_solaCoincideCuandoElActorEsElDelToken()
    {
        var descriptor = FhirRequestParser.Parse(
            "GET", "Appointment?actor=Practitioner/1902&date=ge2026-08-17&_count=100")!;

        FhirIntentMatcher.Match(descriptor, callerPractitionerId: "1902")
            .Intent!.Name.Should().Be("agenda.mine");

        // Con el actor de otro no es "mi agenda": cae en la forma amplia, que exige leer todo.
        FhirIntentMatcher.Match(descriptor, callerPractitionerId: "2030")
            .Intent!.Name.Should().Be("agenda.all");
    }
}
