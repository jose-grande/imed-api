using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Agenda;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Locations;
using IMed.Api.Domain.Practitioners;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Agenda;

/// <summary>
/// Queja recurrente de clientes: recepción mantiene la agenda, pero sin lectura amplia del
/// expediente veía "Paciente" en cada fila, y las clínicas terminaban dándole acceso a todo.
/// <c>agenda.read</c> le muestra quién viene (nombre, identificación, teléfono) y nada clínico.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AgendaTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Rango = "from=2026-11-01T00%3A00%3A00Z&to=2026-12-01T00%3A00%3A00Z";

    private sealed record Org(HttpClient Admin, Guid OrgId, LocationDto Principal, LocationDto Sur);
    private sealed record BootstrapProbe(Guid OrganizationId);

    private async Task<Org> OrganizacionAsync()
    {
        await Task.Delay(800);
        var founderId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(founderId, "duenio", "duenio@imed.test");
        var boot = await (await fixture.CreateClientWithToken(
                fixture.Keycloak.IssueUserToken(founderId, preferredUsername: "duenio"))
            .PostAsJsonAsync("/api/organizations/bootstrap", new { name = "Clínica Agenda", nationalTaxId = "08011990444441" }))
            .Content.ReadFromJsonAsync<BootstrapProbe>();
        var admin = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            founderId, organizationId: boot!.OrganizationId.ToString(), realmRoles: ["org_admin"]));
        var principal = (await admin.GetFromJsonAsync<List<LocationDto>>("/api/locations"))!.Single();
        var sur = (await (await admin.PostAsJsonAsync("/api/locations", new { code = "SUR", name = "Sucursal Sur" }))
            .Content.ReadFromJsonAsync<LocationDto>())!;
        return new Org(admin, boot.OrganizationId, principal, sur);
    }

    /// <summary>Usuario con estos roles, asignado a una sede.</summary>
    private async Task<(Guid UserId, HttpClient Client)> UsuarioAsync(Org org, Guid sede, params string[] roles)
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, $"u{userId:N}", $"u{userId:N}@imed.test");
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var kc = scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>();
            foreach (var r in roles)
            {
                await kc.AssignRealmRoleAsync(userId, r);
            }
        }
        (await org.Admin.PostAsJsonAsync($"/api/locations/{sede}/users/{userId}", new { makePrimary = true }))
            .IsSuccessStatusCode.Should().BeTrue();
        return (userId, fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: org.OrgId.ToString(), realmRoles: roles)));
    }

    /// <summary>Médico con Practitioner, asignado a la sede.</summary>
    private async Task<string> MedicoAsync(Org org, Guid sede)
    {
        var (userId, _) = await UsuarioAsync(org, sede, "practitioner");
        var pracId = $"pr-{userId:N}";
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.UserPractitionerAssignments.Add(UserPractitionerAssignment.Create(userId, org.OrgId, pracId));
        await db.SaveChangesAsync();
        return pracId;
    }

    private string Paciente(string nombre, string dni, string telefono) => fixture.Hapi.SeedResource(new JsonObject
    {
        ["resourceType"] = "Patient",
        ["name"] = new JsonArray(new JsonObject { ["text"] = nombre }),
        ["identifier"] = new JsonArray(new JsonObject
        {
            ["system"] = "https://imed.app/identifiers/hn/dni",
            ["value"] = dni,
            ["type"] = new JsonObject { ["text"] = "Identidad Nacional" },
        }),
        ["telecom"] = new JsonArray(new JsonObject { ["system"] = "phone", ["value"] = telefono }),
        ["birthDate"] = "1990-01-01",
    });

    private string Cita(string patientId, Guid sede, string? pracId = null, string status = "booked",
        string start = "2026-11-26T15:00:00Z") => fixture.Hapi.SeedResource(new JsonObject
    {
        ["resourceType"] = "Appointment",
        ["status"] = status,
        ["start"] = start,
        ["end"] = DateTimeOffset.Parse(start, System.Globalization.CultureInfo.InvariantCulture).AddMinutes(30).ToString("o"),
        ["minutesDuration"] = 30,
        ["description"] = "Dolor torácico desde ayer",
        ["note"] = new JsonArray(new JsonObject { ["text"] = "Paciente con VIH en tratamiento" }),
        ["subject"] = new JsonObject { ["reference"] = $"Patient/{patientId}" },
        ["participant"] = new JsonArray(
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Patient/{patientId}" }, ["status"] = "accepted" },
            pracId is null ? null : new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Practitioner/{pracId}" }, ["status"] = "accepted" },
            new JsonObject { ["actor"] = new JsonObject { ["reference"] = $"Location/{OrganizationLocation.FhirIdFor(sede)}" }, ["status"] = "accepted" }),
    });

    [Fact]
    public async Task Recepcion_veQuienViene_sinNadaClinico_ySoloSusSedes()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var maria = Paciente("María López", "0801199012345", "9999-0000");
        var jose = Paciente("José Ruiz", "0801198800001", "9888-1111");
        var aqui = Cita(maria, org.Principal.Id);
        Cita(jose, org.Sur.Id);

        var resp = await recepcion.GetAsync($"/api/agenda/appointments?{Rango}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var agenda = (await resp.Content.ReadFromJsonAsync<List<AgendaAppointmentDto>>())!;

        var cita = agenda.Should().ContainSingle("la cita de la Sucursal Sur no es de su sede").Subject;
        cita.Id.Should().Be(aqui);
        cita.Patient!.DisplayName.Should().Be("María López");
        cita.Patient.IdentifierValue.Should().Be("0801199012345");
        cita.Patient.Phone.Should().Be("9999-0000");
        cita.Description.Should().BeNull("el motivo puede tener información clínica");
        cita.Note.Should().BeNull();
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("VIH").And.NotContain("torácico");
    }

    [Fact]
    public async Task QuienLeeElExpediente_veElMotivo_yTodasLasSedes()
    {
        var org = await OrganizacionAsync();
        Cita(Paciente("María López", "1", "1"), org.Principal.Id);
        Cita(Paciente("José Ruiz", "2", "2"), org.Sur.Id);

        var agenda = (await org.Admin.GetFromJsonAsync<List<AgendaAppointmentDto>>($"/api/agenda/appointments?{Rango}"))!;

        agenda.Should().HaveCount(2);
        agenda.Should().OnlyContain(c => c.Description == "Dolor torácico desde ayer");
    }

    [Fact]
    public async Task Recepcion_noPidelaAgendaDeOtraSede_yCajaNoVeLaAgenda()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var (_, caja) = await UsuarioAsync(org, org.Principal.Id, "cashier");

        (await recepcion.GetAsync($"/api/agenda/appointments?{Rango}&locationId={org.Sur.Id}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await caja.GetAsync($"/api/agenda/appointments?{Rango}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "agenda.read no es de caja por defecto");
    }

    [Fact]
    public async Task CambiarElEstado_noTocaElMotivoNiLasNotas()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var cita = Cita(Paciente("María López", "1", "1"), org.Principal.Id);

        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cita}/status", new { status = "arrived" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var guardada = fixture.Hapi.GetResource("Appointment", cita)!;
        ((string?)guardada["status"]).Should().Be("arrived");
        ((string?)guardada["description"]).Should().Be("Dolor torácico desde ayer");
        ((string?)guardada["note"]![0]!["text"]).Should().Be("Paciente con VIH en tratamiento");

        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cita}/status", new { status = "fulfilled" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "cumplir una cita es del cierre de la consulta, no de la agenda");
    }

    [Fact]
    public async Task Cancelar_cancelaElEncuentroDePreclinica()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var cita = Cita(Paciente("A", "1", "1"), org.Principal.Id, status: "checked-in");
        var encuentro = fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "Encounter",
            ["status"] = "planned",
            ["appointment"] = new JsonArray(new JsonObject { ["reference"] = $"Appointment/{cita}" }),
        });

        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cita}/status", new { status = "cancelled" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        ((string?)fixture.Hapi.GetResource("Encounter", encuentro)!["status"]).Should().Be("cancelled",
            "recepción no puede buscar encuentros por el proxy; lo hace el servidor");
    }

    [Fact]
    public async Task UnaCitaCerrada_oDeOtraSede_noSeMueve()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var cancelada = Cita(Paciente("A", "1", "1"), org.Principal.Id, status: "cancelled");
        var ajena = Cita(Paciente("B", "2", "2"), org.Sur.Id);

        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cancelada}/status", new { status = "arrived" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{ajena}/status", new { status = "cancelled" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reprogramar_cambiaHorarioYMedico_yConservaElResto()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var drA = await MedicoAsync(org, org.Principal.Id);
        var drB = await MedicoAsync(org, org.Principal.Id);
        var paciente = Paciente("María López", "1", "1");
        var cita = Cita(paciente, org.Principal.Id, drA);

        var resp = await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cita}/reschedule", new
        {
            start = "2026-11-27T16:00:00Z",
            minutesDuration = 45,
            practitionerFhirId = drB,
            consultationType = "follow-up",
            description = (string?)null,
            note = (string?)null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, await resp.Content.ReadAsStringAsync());

        var guardada = fixture.Hapi.GetResource("Appointment", cita)!;
        DateTimeOffset.Parse((string)guardada["start"]!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(new DateTimeOffset(2026, 11, 27, 16, 0, 0, TimeSpan.Zero));
        ((int)guardada["minutesDuration"]!).Should().Be(45);
        var actores = ((JsonArray)guardada["participant"]!).Select(p => (string?)p!["actor"]!["reference"]).ToList();
        actores.Should().Contain($"Practitioner/{drB}").And.NotContain($"Practitioner/{drA}");
        actores.Should().Contain($"Patient/{paciente}").And.Contain($"Location/{OrganizationLocation.FhirIdFor(org.Principal.Id)}");
        ((string?)guardada["description"]).Should().Be("Dolor torácico desde ayer", "recepción no mandó el motivo: se conserva");
        ((string?)guardada["appointmentType"]!["coding"]![0]!["code"]).Should().Be("follow-up");
    }

    [Fact]
    public async Task Reprogramar_conUnMedicoDeOtraSede_oUnaCitaQueYaLlego_seRechaza()
    {
        var org = await OrganizacionAsync();
        var (_, recepcion) = await UsuarioAsync(org, org.Principal.Id, "auxiliar");
        var drAqui = await MedicoAsync(org, org.Principal.Id);
        var drSur = await MedicoAsync(org, org.Sur.Id);
        var cita = Cita(Paciente("A", "1", "1"), org.Principal.Id, drAqui);
        var llego = Cita(Paciente("B", "2", "2"), org.Principal.Id, drAqui, status: "arrived");

        object Cuerpo(string pr) => new { start = "2026-11-27T16:00:00Z", minutesDuration = 30, practitionerFhirId = pr };

        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{cita}/reschedule", Cuerpo(drSur)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "el médico no atiende en la sede de la cita");
        (await recepcion.PostAsJsonAsync($"/api/agenda/appointments/{llego}/reschedule", Cuerpo(drAqui)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
