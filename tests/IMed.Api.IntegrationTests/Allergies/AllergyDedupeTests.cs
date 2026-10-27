using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using IMed.Api.Application.Allergies;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Allergies;

/// <summary>
/// Limpieza de las alergias duplicadas que dejó la preclínica antes de P-1 (staging las tiene).
/// Reporta en seco por defecto; al aplicar, marca como error —no borra— y conserva la confirmada
/// más antigua.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class AllergyDedupeTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient ClientAs(Guid orgId, string role = "org_admin")
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, organizationId: orgId.ToString(), realmRoles: [role]));
    }

    private string Seed(string patient, string? code, string text, string recorded, string verification = "unconfirmed") =>
        fixture.Hapi.SeedResource(new JsonObject
        {
            ["resourceType"] = "AllergyIntolerance",
            ["clinicalStatus"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = "active" }) },
            ["verificationStatus"] = new JsonObject { ["coding"] = new JsonArray(new JsonObject { ["code"] = verification }) },
            ["code"] = code is null
                ? new JsonObject { ["text"] = text }
                : new JsonObject
                {
                    ["coding"] = new JsonArray(new JsonObject
                    {
                        ["system"] = "http://snomed.info/sct", ["code"] = code, ["display"] = text,
                    }),
                    ["text"] = text,
                },
            ["patient"] = new JsonObject { ["reference"] = $"Patient/{patient}" },
            ["recordedDate"] = recorded,
        });

    private (string PenOld, string PenConfirmed, string PenNew, string MarA, string MarB, string Unique, string OtherPatient) SeedScenario() => (
        Seed("p1", "764146007", "Penicilina", "2026-08-01T10:00:00Z"),
        Seed("p1", "764146007", "Penicilina", "2026-08-15T10:00:00Z", verification: "confirmed"),
        Seed("p1", "764146007", "Penicilina", "2026-09-01T10:00:00Z"),
        Seed("p1", null, "Mariscos", "2026-08-02T10:00:00Z"),
        Seed("p1", null, "  MARISCOS ", "2026-09-02T10:00:00Z"),
        Seed("p1", null, "Látex", "2026-08-03T10:00:00Z"),
        Seed("p2", "764146007", "Penicilina", "2026-08-04T10:00:00Z"));

    [Fact]
    public async Task EnSeco_reportaSinTocarNada()
    {
        var s = SeedScenario();
        using var client = ClientAs(Guid.NewGuid());

        var report = await (await client.PostAsync("/api/maintenance/allergies/dedupe", null))
            .Content.ReadFromJsonAsync<AllergyDedupeReport>();

        report!.Applied.Should().BeFalse();
        report.DuplicatesFound.Should().Be(3);
        var pen = report.Groups.Single(g => g.Display == "Penicilina");
        pen.KeptId.Should().Be(s.PenConfirmed, "la confirmada manda sobre la más antigua sin confirmar");
        pen.DuplicateIds.Should().BeEquivalentTo([s.PenOld, s.PenNew]);
        report.Groups.Single(g => g.PatientFhirId == "p1" && g.KeptId == s.MarA).DuplicateIds.Should().Equal(s.MarB);
        fixture.Hapi.TransactionCount.Should().Be(0);
    }

    [Fact]
    public async Task AlAplicar_marcaComoError_sinBorrar_yDejaRastro()
    {
        var orgId = Guid.NewGuid();
        var s = SeedScenario();
        using var client = ClientAs(orgId);

        var resp = await client.PostAsync("/api/maintenance/allergies/dedupe?apply=true", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var id in new[] { s.PenOld, s.PenNew, s.MarB })
        {
            var marked = fixture.Hapi.GetResource("AllergyIntolerance", id)!;
            ((string?)marked["verificationStatus"]!["coding"]![0]!["code"]).Should().Be("entered-in-error");
            marked["clinicalStatus"].Should().BeNull("R5 no admite estado clínico en lo cargado por error");
        }
        foreach (var id in new[] { s.PenConfirmed, s.MarA, s.Unique, s.OtherPatient })
        {
            ((string?)fixture.Hapi.GetResource("AllergyIntolerance", id)!["meta"]!["versionId"]).Should().Be("1");
        }

        // Aplicar de nuevo no encuentra nada: lo marcado ya no cuenta.
        var again = await (await client.PostAsync("/api/maintenance/allergies/dedupe?apply=true", null))
            .Content.ReadFromJsonAsync<AllergyDedupeReport>();
        again!.DuplicatesFound.Should().Be(0);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var actions = await db.AppAuditLog
            .Where(x => x.OrganizationId == orgId && x.Category == "maintenance")
            .Select(x => x.Action).ToListAsync();
        actions.Should().Contain("Apply:AllergyDedupe");
    }

    [Fact]
    public async Task SoloLaAdministraQuienAdministraLaOrganizacion()
    {
        using var client = ClientAs(Guid.NewGuid(), role: "practitioner");

        (await client.PostAsync("/api/maintenance/allergies/dedupe", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
