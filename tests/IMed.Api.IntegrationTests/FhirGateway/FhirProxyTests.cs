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
    public async Task UnSignoVital_llegaAHapiYQuedaRegistradoComoPermitido()
    {
        // Prueba de cableado, no de política: la regla de categoría solo funciona si el proxy
        // efectivamente lee el cuerpo en un POST de Observation. Si `RequiresBody` dejara de
        // decir que sí, el evaluador recibiría null y negaría todos los signos vitales — que es
        // exactamente el modo de falla que esta corrección vino a eliminar.
        var (client, orgId, _) = await AuthedAsync("auxiliar");

        const string vital = """
        {"resourceType":"Observation","status":"final",
         "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/observation-category","code":"vital-signs"}]}],
         "code":{"coding":[{"system":"http://loinc.org","code":"29463-7"}]},
         "subject":{"reference":"Patient/p1"},
         "valueQuantity":{"value":70,"unit":"kg"}}
        """;

        var resp = await client.PostAsync("/fhir/Observation",
            new StringContent(vital, System.Text.Encoding.UTF8, "application/fhir+json"));

        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registro = await db.AppAuditLog.AsNoTracking()
            .Where(x => x.OrganizationId == orgId && x.Category == FhirProxyEndpoint.PhiAuditCategory)
            .OrderByDescending(x => x.OccurredAt)
            .FirstAsync();

        JsonDocument.Parse(registro.Payload).RootElement
            .GetProperty("allowed").GetBoolean().Should().BeTrue(
                "el cuerpo tiene que haber llegado al evaluador para que la categoría se pueda mirar");
    }

    [Fact]
    public async Task UnResultadoDeLaboratorio_seRegistraComoNegadoAunqueEnSombraPase()
    {
        var (client, orgId, _) = await AuthedAsync("lab_supervisor");

        const string resultado = """
        {"resourceType":"Observation","status":"final",
         "category":[{"coding":[{"system":"http://terminology.hl7.org/CodeSystem/observation-category","code":"laboratory"}]}],
         "code":{"coding":[{"system":"http://loinc.org","code":"2345-7"}]},
         "subject":{"reference":"Patient/p1"},
         "valueQuantity":{"value":95,"unit":"mg/dL"}}
        """;

        await client.PostAsync("/fhir/Observation",
            new StringContent(resultado, System.Text.Encoding.UTF8, "application/fhir+json"));

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var registro = await db.AppAuditLog.AsNoTracking()
            .Where(x => x.OrganizationId == orgId && x.Category == FhirProxyEndpoint.PhiAuditCategory)
            .OrderByDescending(x => x.OccurredAt)
            .FirstAsync();

        var payload = JsonDocument.Parse(registro.Payload).RootElement;
        payload.GetProperty("allowed").GetBoolean().Should().BeFalse(
            "el circuito de laboratorio sigue cerrado desde el lado público");
        payload.GetProperty("reason").GetString().Should().Contain("circuito de laboratorio");
    }

    [Fact]
    public async Task EnModoEnforce_loNoPermitido_seBloqueaCon403()
    {
        // La contracara del test de sombra, y la que dice si el proxy sirve para algo. La suite
        // corre en sombra para que las demás pruebas midan la política y no el bloqueo; esta
        // levanta un host con `Enforce` para verificar el modo que de verdad se publica.
        await Task.Delay(500);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "tecnico", "tecnico@imed.test");
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: Guid.NewGuid().ToString(), realmRoles: ["auxiliar"]);

        using var enforcing = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<FhirGatewayOptions>(o => o.Mode = FhirGatewayMode.Enforce)));

        using var client = enforcing.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/fhir/DiagnosticReport/dr-1");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "en Enforce la política bloquea, que es el punto de salir de sombra");
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
