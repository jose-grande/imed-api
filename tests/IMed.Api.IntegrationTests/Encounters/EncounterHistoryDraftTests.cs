using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Encounters;

/// <summary>
/// Borrador de antecedentes de la consulta (Épica P, P-D3): sobrevive a un refresco, no deja que
/// dos pestañas se pisen y, al cerrar, se marca en vez de borrarse.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class EncounterHistoryDraftTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record DraftDto(string EncounterFhirId, string PatientFhirId, JsonElement Payload, int Version,
        DateTimeOffset? CommittedAt, string? ProvenanceFhirId);

    private HttpClient ClientAs(Guid orgId, string role = "practitioner")
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, organizationId: orgId.ToString(), realmRoles: [role]));
    }

    private static object Save(int expectedVersion, string allergy = "Penicilina") => new
    {
        patientFhirId = "pat-1",
        payload = new { allergies = new[] { new { kind = "add", display = allergy } } },
        expectedVersion,
    };

    [Fact]
    public async Task ElBorrador_seGuardaYSeRecupera()
    {
        using var client = ClientAs(Guid.NewGuid());

        (await client.GetAsync("/api/encounters/enc-1/history-draft")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var saved = await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0));
        saved.StatusCode.Should().Be(HttpStatusCode.OK);

        var draft = await client.GetFromJsonAsync<DraftDto>("/api/encounters/enc-1/history-draft");
        draft!.Version.Should().Be(1);
        draft.Payload.GetProperty("allergies")[0].GetProperty("display").GetString().Should().Be("Penicilina");
    }

    [Fact]
    public async Task DosPestanas_noSePisan()
    {
        using var client = ClientAs(Guid.NewGuid());
        await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0));

        // La segunda pestaña leyó la versión 0 y guarda encima de un cambio que no vio.
        var stale = await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0, "Látex"));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task AlCerrar_seMarca_noSeBorra_yNoAdmiteMasCambios()
    {
        using var client = ClientAs(Guid.NewGuid());
        await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0));

        var commit = await client.PostAsJsonAsync("/api/encounters/enc-1/history-draft/commit", new { provenanceFhirId = "prov-9" });
        commit.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var draft = await client.GetFromJsonAsync<DraftDto>("/api/encounters/enc-1/history-draft");
        draft!.CommittedAt.Should().NotBeNull();
        draft.ProvenanceFhirId.Should().Be("prov-9");

        var late = await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(1, "Látex"));
        late.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // El cierre puede reintentarse: marcar dos veces no falla.
        (await client.PostAsJsonAsync("/api/encounters/enc-1/history-draft/commit", new { provenanceFhirId = "prov-9" }))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ElBorradorDeOtraOrganizacion_noSeVe()
    {
        using var mine = ClientAs(Guid.NewGuid());
        using var other = ClientAs(Guid.NewGuid());
        await mine.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0));

        (await other.GetAsync("/api/encounters/enc-1/history-draft")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SinPermisoDeEscritura_noHayBorrador()
    {
        using var client = ClientAs(Guid.NewGuid(), role: "lab_tech");

        (await client.PutAsJsonAsync("/api/encounters/enc-1/history-draft", Save(0)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
