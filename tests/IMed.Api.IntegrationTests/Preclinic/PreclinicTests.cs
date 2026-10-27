using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Preclinic;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Preclinic;

/// <summary>
/// Preclínica por imed-api (Épica P-1).
///
/// <para>El bug de staging que origina esto: la consulta mostraba signos vitales y alergias
/// repetidos. Los signos vitales no tenían encuentro —la consulta tomaba "los de las últimas 24
/// horas sin encuentro", que mezclaba visitas— y la preclínica no veía las alergias existentes, así
/// que las recreaba en cada visita. Estas pruebas fijan las dos reglas que lo cierran: la toma nace
/// atada al encuentro de la cita, y lo ya registrado no se vuelve a crear.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PreclinicTests(ImedApiFixture fixture) : IAsyncLifetime
{
    private const string Patient = "pat-pre-1";

    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient ClientAs(string role, Guid? orgId = null, string? practitionerId = "prac-aux-1")
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: (orgId ?? Guid.NewGuid()).ToString(),
            practitionerId: practitionerId,
            realmRoles: [role]));
    }

    private string SeedAllergy(string? code, string text, string clinical = "active")
    {
        var codeNode = new JsonObject { ["text"] = text };
        if (code is not null)
        {
            codeNode["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://snomed.info/sct", ["code"] = code, ["display"] = text,
            });
        }
        return fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = new JsonObject
            {
                ["coding"] = new JsonArray(new JsonObject
                {
                    ["system"] = "http://terminology.hl7.org/CodeSystem/allergyintolerance-clinical", ["code"] = clinical,
                }),
            },
            ["code"] = codeNode,
            ["patient"] = new JsonObject { ["reference"] = $"Patient/{Patient}" },
        });
    }

    private static object Vitals(decimal weight = 70m) => new
    {
        vitals = new { weightKg = weight, systolicBp = 120m, diastolicBp = 80m },
        catalogAllergies = Array.Empty<object>(),
        freeTextAllergies = Array.Empty<string>(),
        noKnownAllergies = false,
    };

    private static string? Ref(JsonObject r, string property) => (string?)r[property]?["reference"];

    // ---- Contexto --------------------------------------------------------------------------------

    [Fact]
    public async Task ElContexto_muestraLasAlergiasRegistradas_sinLasNegaciones()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        SeedAllergy("764146007", "Penicilina");
        SeedAllergy("716186003", "Sin alergias conocidas", clinical: "inactive"); // inactiva: no aparece
        SeedAllergy(null, "Mariscos");
        using var client = ClientAs("auxiliar");

        var resp = await client.GetAsync($"/api/preclinic/appointments/{appt}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<PreclinicContextDto>();
        dto!.PatientFhirId.Should().Be(Patient);
        dto.EncounterFhirId.Should().BeNull();
        dto.Allergies.Select(a => a.Display).Should().BeEquivalentTo(["Penicilina", "Mariscos"]);
        dto.NoKnownAllergies.Should().BeFalse();
    }

    [Fact]
    public async Task UnaCitaFueraDeLaFila_noDejaLeerNadaDelPaciente()
    {
        // Es la frontera del permiso: `record.preclinic` no es lectura del expediente, es "el
        // paciente que tengo enfrente". Una cita agendada para mañana no es eso.
        var appt = fixture.Hapi.SeedAppointment("booked", Patient);
        SeedAllergy("764146007", "Penicilina");
        using var client = ClientAs("auxiliar");

        var resp = await client.GetAsync($"/api/preclinic/appointments/{appt}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UnRolSinPermisoDePreclinica_recibe403()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("lab_tech");

        var resp = await client.GetAsync($"/api/preclinic/appointments/{appt}");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- Envío: el encuentro -----------------------------------------------------------------------

    [Fact]
    public async Task LaPreclinica_abreElEncuentroDeLaCita_yLosSignosVitalesNacenAtadosAEl()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar");

        var resp = await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals());

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();
        result!.ObservationsCreated.Should().Be(3);

        var encounter = fixture.Hapi.GetResource("Encounter", result.EncounterFhirId)!;
        ((string?)encounter["status"]).Should().Be("planned", "el médico todavía no entró: sigue viendo \"Atender\"");
        ((string?)encounter["appointment"]![0]!["reference"]).Should().Be($"Appointment/{appt}");
        encounter["participant"].Should().BeNull("el médico se agrega cuando pulsa Atender");

        var observations = fixture.Hapi.Resources("Observation");
        observations.Should().HaveCount(3);
        observations.Should().OnlyContain(o => Ref(o, "encounter") == $"Encounter/{result.EncounterFhirId}");
        observations.Should().OnlyContain(o => (string?)o["performer"]![0]!["reference"] == "Practitioner/prac-aux-1");

        ((string?)fixture.Hapi.GetResource("Appointment", appt)!["status"]).Should().Be("checked-in");
        fixture.Hapi.TransactionCount.Should().Be(1, "todo en una sola transacción");
    }

    [Fact]
    public async Task RepetirLaToma_reusaElMismoEncuentro()
    {
        // J-D6: la auxiliar puede volver a tomar los signos (el primer registro estaba mal). Eso no
        // puede abrir un segundo encuentro para la misma cita.
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar");

        var first = await (await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals(70m)))
            .Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();
        var second = await (await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals(71m)))
            .Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();

        second!.EncounterFhirId.Should().Be(first!.EncounterFhirId);
        fixture.Hapi.Resources("Encounter").Should().HaveCount(1);

        var ctx = await client.GetFromJsonAsync<PreclinicContextDto>($"/api/preclinic/appointments/{appt}");
        ctx!.EncounterFhirId.Should().Be(first.EncounterFhirId);
        ctx.Vitals.Should().HaveCount(6, "las dos tomas quedan; la consulta muestra la más reciente de cada signo");
    }

    [Fact]
    public async Task SiElMedicoYaEntro_laTomaSeSumaASuConsulta()
    {
        var appt = fixture.Hapi.SeedAppointment("checked-in", Patient);
        var enc = fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = "in-progress",
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{Patient}" },
            ["appointment"] = new JsonArray(new JsonObject { ["reference"] = $"Appointment/{appt}" }),
        });
        using var client = ClientAs("auxiliar");

        var result = await (await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals()))
            .Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();

        result!.EncounterFhirId.Should().Be(enc);
        fixture.Hapi.Resources("Encounter").Should().HaveCount(1);
    }

    [Fact]
    public async Task LaTransaccion_creaElEncuentroConCreacionCondicional()
    {
        // Dos envíos simultáneos de la misma preclínica no pueden dejar dos encuentros: lo impide
        // HAPI con `ifNoneExist`, no una búsqueda previa que ambos pasarían a la vez.
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar");

        await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals());

        var request = fixture.Hapi.LastTransaction!["entry"]![0]!["request"]!;
        ((string?)request["ifNoneExist"]).Should().Be($"appointment=Appointment/{appt}&status=planned,in-progress");
        var apptPut = fixture.Hapi.LastTransaction!["entry"]!.AsArray()
            .Single(e => (string?)e!["request"]!["url"] == $"Appointment/{appt}")!;
        ((string?)apptPut["request"]!["ifMatch"]).Should().Be("W/\"1\"");
    }

    // ---- Envío: alergias -------------------------------------------------------------------------

    [Fact]
    public async Task LoYaRegistrado_noSeVuelveACrear_niPorCodigoNiPorTexto()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        SeedAllergy("764146007", "Penicilina");
        SeedAllergy(null, "Mariscos");
        using var client = ClientAs("auxiliar");

        var resp = await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", new
        {
            catalogAllergies = new[]
            {
                new { code = "764146007", display = "Penicilina", category = "medication" }, // mismo código
                new { code = "91935009", display = "Maní", category = "food" },                // nueva
            },
            freeTextAllergies = new[] { "  MARISCOS ", "Látex", "latex" }, // texto ya registrado + repetido en el envío
            noKnownAllergies = false,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();
        result!.AllergiesCreated.Should().Be(2);
        result.SkippedDuplicates.Should().BeEquivalentTo(["Penicilina", "MARISCOS", "latex"]);

        var created = fixture.Hapi.Resources("AllergyIntolerance")
            .Where(a => (string?)a["meta"]!["versionId"] == "1" && a["participant"] is not null)
            .ToList();
        created.Select(a => (string?)a["code"]!["text"]).Should().BeEquivalentTo(["Maní", "Látex"]);
        created.Should().OnlyContain(a => (string?)a["verificationStatus"]!["coding"]![0]!["code"] == "unconfirmed",
            "lo declarado en preclínica lo confirma el médico");
        created.Should().OnlyContain(a => (string?)a["participant"]![0]!["actor"]!["reference"] == "Practitioner/prac-aux-1",
            "R5: quien registra va en participant, no en recorder (HAPI rechaza recorder con 422)");
        created.Should().OnlyContain(a => a["recorder"] == null);
    }

    [Fact]
    public async Task SinAlergiasConocidas_seRegistra_yUnaAlergiaPosteriorLaInactiva()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar");

        var nka = await (await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", new
        {
            noKnownAllergies = true,
        })).Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();
        nka!.NoKnownAllergiesRecorded.Should().BeTrue();

        var ctx = await client.GetFromJsonAsync<PreclinicContextDto>($"/api/preclinic/appointments/{appt}");
        ctx!.NoKnownAllergies.Should().BeTrue();
        ctx.Allergies.Should().BeEmpty();

        await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", new
        {
            freeTextAllergies = new[] { "Ibuprofeno" },
            noKnownAllergies = false,
        });

        var negation = fixture.Hapi.Resources("AllergyIntolerance")
            .Single(a => (string?)a["code"]!["coding"]?[0]?["code"] == PreclinicFhir.NoKnownAllergyCode);
        ((string?)negation["clinicalStatus"]!["coding"]![0]!["code"]).Should().Be("inactive",
            "dejó de ser cierto, pero que alguna vez se afirmó es parte de la historia");

        ctx = await client.GetFromJsonAsync<PreclinicContextDto>($"/api/preclinic/appointments/{appt}");
        ctx!.NoKnownAllergies.Should().BeFalse();
        ctx.Allergies.Select(a => a.Display).Should().BeEquivalentTo(["Ibuprofeno"]);
    }

    [Fact]
    public async Task SinAlergiasConocidas_conAlergiasActivas_esUnaContradiccion()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        SeedAllergy("764146007", "Penicilina");
        using var client = ClientAs("auxiliar");

        var resp = await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", new { noKnownAllergies = true });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Hapi.TransactionCount.Should().Be(0);
    }

    // ---- Envío: medicación habitual (P-2) --------------------------------------------------------

    private string SeedMedication(string? atc, string display, string status = "recorded", string? adherence = null)
    {
        var concept = new JsonObject { ["text"] = display };
        if (atc is not null)
        {
            concept["coding"] = new JsonArray(new JsonObject
            {
                ["system"] = "http://www.whocc.no/atc", ["code"] = atc, ["display"] = display,
            });
        }
        var statement = new JsonObject
        {
            ["resourceType"] = "MedicationStatement",
            ["status"] = status,
            ["medication"] = new JsonObject { ["concept"] = concept },
            ["subject"] = new JsonObject { ["reference"] = $"Patient/{Patient}" },
        };
        if (adherence is not null)
        {
            statement["adherence"] = new JsonObject
            {
                ["code"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = adherence }) },
            };
        }
        return fixture.Hapi.SeedResource(statement);
    }

    [Fact]
    public async Task LaMedicacionDeclarada_entraEnBorrador_sinRepetirLaQueYaToma()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        SeedMedication("C09AA02", "Enalapril");
        SeedMedication(null, "Omeprazol", adherence: "stopped"); // ya no lo toma: no cuenta
        using var client = ClientAs("auxiliar");

        var ctx = await client.GetFromJsonAsync<PreclinicContextDto>($"/api/preclinic/appointments/{appt}");
        ctx!.Medications.Select(m => m.Display).Should().Equal("Enalapril");

        var resp = await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", new
        {
            noKnownAllergies = false,
            medications = new object[]
            {
                new { atcCode = "C09AA02", display = "Enalapril 10 mg" },              // mismo ATC
                new { atcCode = (string?)null, display = "omeprazol", dosageText = "20 mg en ayunas" }, // lo retomó
                new { atcCode = "A10BA02", display = "Metformina", dosageText = "850 mg c/12 h" },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreclinicSubmitResultDto>();
        result!.MedicationsCreated.Should().Be(2);
        result.SkippedDuplicates.Should().Equal("Enalapril 10 mg");

        var created = fixture.Hapi.Resources("MedicationStatement").Where(m => m["dateAsserted"] is not null).ToList();
        created.Should().OnlyContain(m => (string?)m["status"] == "draft", "lo concilia el médico");
        created.Should().OnlyContain(m => Ref(m, "encounter") == $"Encounter/{result.EncounterFhirId}");
        ((string?)created.Single(m => (string?)m["medication"]!["concept"]!["text"] == "Metformina")["dosage"]![0]!["text"])
            .Should().Be("850 mg c/12 h");
    }

    // ---- Validación y rastro ---------------------------------------------------------------------

    [Fact]
    public async Task UnSignoVitalAbsurdo_seRechazaSinEscribirNada()
    {
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar");

        // 70 000: el peso cargado en gramos.
        var resp = await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals(70000m));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fixture.Hapi.TransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task LeerYEscribir_quedaEnLaBitacoraDeAccesoAPHI()
    {
        // No pasa por el proxy FHIR, así que el proxy no lo registra: que la lectura entre por otra
        // puerta no puede significar que no quede rastro.
        var orgId = Guid.NewGuid();
        var appt = fixture.Hapi.SeedAppointment("arrived", Patient);
        using var client = ClientAs("auxiliar", orgId);

        await client.GetAsync($"/api/preclinic/appointments/{appt}");
        await client.PostAsJsonAsync($"/api/preclinic/appointments/{appt}", Vitals());

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AppAuditLog
            .Where(x => x.OrganizationId == orgId && x.Category == "phi_access" && x.Target == $"Appointment/{appt}")
            .Select(x => x.Action)
            .ToListAsync();
        actions.Should().BeEquivalentTo(["Read:Preclinic", "Create:Preclinic"]);
    }
}
