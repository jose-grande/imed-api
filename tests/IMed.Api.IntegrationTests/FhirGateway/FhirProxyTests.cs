using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using IMed.Api.WebApi.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.FhirGateway;

/// <summary>
/// Proxy autorizador de <c>/fhir/**</c> (ADR-007, SEC-3 parte 2).
///
/// <para>Cubre las tres propiedades del transporte: que el request llegue a HAPI, que el modo
/// sombra registre sin bloquear, y que todo acceso quede en la bitácora de PHI — que es lo que
/// permite contestar "¿quién leyó este expediente?".</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class FhirProxyTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HttpClient Client, Guid OrgId, Guid UserId)> AuthedAsync(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "usuario", "usuario@imed.test");
        var orgId = Guid.NewGuid();
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return (fixture.CreateClientWithToken(token), orgId, userId);
    }

    [Fact]
    public async Task UnaLecturaPermitida_llegaAHapi()
    {
        var (client, _, _) = await AuthedAsync("practitioner");

        var resp = await client.GetAsync("/fhir/Patient/pat-proxy-1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "el proxy es transparente: /fhir sigue siendo un endpoint FHIR conforme");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Patient");
    }

    [Fact]
    public async Task SinOrganizacionEnElToken_seRechaza()
    {
        await Task.Delay(300);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var client = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "sin-org"));

        (await client.GetAsync("/fhir/Patient/x")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "sin organización no hay política contra la cual evaluar");
    }

    [Fact]
    public async Task EnModoSombra_loQueSeHabriaBloqueado_pasaIgual()
    {
        // El auxiliar no tiene permiso para leer resultados. En sombra igual pasa, y el registro
        // deja constancia de que se habría bloqueado. Es el punto del modo: descubrir los olvidos
        // de la lista blanca en un log y no rompiendo una pantalla.
        var (client, orgId, _) = await AuthedAsync("auxiliar");

        var resp = await client.GetAsync("/fhir/DiagnosticReport/dr-inexistente");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "en sombra la política no bloquea");

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registro = await db.AppAuditLog.AsNoTracking()
            .Where(x => x.OrganizationId == orgId && x.Category == FhirProxyEndpoint.PhiAuditCategory)
            .OrderByDescending(x => x.OccurredAt)
            .FirstOrDefaultAsync();

        registro.Should().NotBeNull("el intento tiene que quedar registrado aunque se deje pasar");
        // El payload es jsonb: Postgres normaliza el espaciado, así que se afirma sobre el JSON
        // parseado y no sobre el texto crudo.
        var payload = JsonDocument.Parse(registro!.Payload).RootElement;
        payload.GetProperty("allowed").GetBoolean().Should().BeFalse();
        payload.GetProperty("mode").GetString().Should().Be("Shadow");
    }

    [Fact]
    public async Task TodoAccesoQuedaEnLaBitacoraDePhi_conActorRecursoYResultado()
    {
        var (client, orgId, userId) = await AuthedAsync("practitioner");

        await client.GetAsync("/fhir/Patient/pat-auditado");

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registro = await db.AppAuditLog.AsNoTracking()
            .Where(x => x.OrganizationId == orgId && x.Category == FhirProxyEndpoint.PhiAuditCategory)
            .OrderByDescending(x => x.OccurredAt)
            .FirstAsync();

        // Es la pregunta que hoy no tiene respuesta: quién leyó qué expediente y cuándo.
        registro.ActorUserId.Should().Be(userId);
        registro.Target.Should().Be("Patient/pat-auditado");
        registro.Action.Should().Be("Read:Patient");
        JsonDocument.Parse(registro.Payload).RootElement
            .GetProperty("allowed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task ElProxyRespondeConServerTiming()
    {
        var (client, _, _) = await AuthedAsync("practitioner");

        var resp = await client.GetAsync("/fhir/Patient/pat-timing");

        resp.Headers.TryGetValues("Server-Timing", out var values).Should().BeTrue(
            "sin medir, la discusión sobre el costo del hop es una opinión");
        values!.Should().ContainSingle().Which.Should().StartWith("gw;dur=");
    }
}
