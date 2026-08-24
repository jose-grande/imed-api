using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabOrders;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.LabOrders;

/// <summary>
/// Gateway de órdenes del médico (LIS-RD-09): crear/listar órdenes pasa por
/// imed-api (no directo a HAPI). Cierra la deuda de seguridad del frontend.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabOrdersGatewayTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(userId, organizationId: Guid.NewGuid().ToString(), realmRoles: ["org_admin"]);
        return fixture.CreateClientWithToken(token);
    }

    [Fact]
    public async Task CreateYListarPorEncounter_porElGateway()
    {
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var body = new
        {
            patientFhirId = "pat-go-1",
            encounterFhirId = enc,
            requesterPractitionerId = (string?)null,
            status = "draft",
            priority = "routine",
            loincCode = (string?)null,
            localCode = "CMP",
            localSystem = "http://imed.local/panel",
            codeText = "Perfil metabolico completo",
            codeDisplay = (string?)null,
            occurrenceDateTime = (string?)null,
            reasonText = (string?)null,
            note = (string?)null,
            requisition = enc,
        };

        var create = await client.PostAsJsonAsync("/api/lab-orders", body);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await create.Content.ReadFromJsonAsync<LabOrderDto>();
        dto!.Id.Should().NotBeNullOrWhiteSpace();
        dto.Status.Should().Be("draft");
        dto.LocalCode.Should().Be("CMP");

        var list = await (await client.GetAsync($"/api/lab-orders/by-encounter/{enc}"))
            .Content.ReadFromJsonAsync<LabOrderDto[]>();
        list!.Should().Contain(o => o.Id == dto.Id);
    }

    [Fact]
    public async Task ElCodigoDelPanel_noVuelveDisfrazadoDeLoinc()
    {
        // `CMP` es el código del panel de la clínica, no un código LOINC. Al leer la orden, el
        // gateway tomaba el primer coding sin mirar su `system` y lo devolvía como `loincCode`;
        // el cliente lo reconstruía bajo `http://loinc.org` y el guardado siguiente —promover
        // draft → active al cerrar la consulta— lo escribía así en el expediente.
        //
        // El daño es que un código local queda publicado como estándar internacional: cualquiera
        // que lea ese ServiceRequest por FHIR resuelve `CMP` contra LOINC y no encuentra nada, o
        // peor, encuentra otra cosa.
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/lab-orders", new
        {
            patientFhirId = "pat-panel-1",
            encounterFhirId = enc,
            status = "draft",
            localCode = "CMP",
            localSystem = "http://imed.local/panel",
            codeText = "Perfil metabolico completo",
            requisition = enc,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var creado = await create.Content.ReadFromJsonAsync<LabOrderDto>();

        // Por id y por búsqueda: son dos caminos de lectura distintos y el error estaba en el
        // parseo que ambos comparten, así que ambos tienen que sostener la distinción.
        var porId = await (await client.GetAsync($"/api/lab-orders/{creado!.Id}"))
            .Content.ReadFromJsonAsync<LabOrderDto>();
        var porEncuentro = (await (await client.GetAsync($"/api/lab-orders/by-encounter/{enc}"))
            .Content.ReadFromJsonAsync<LabOrderDto[]>())!.Single(o => o.Id == creado.Id);

        foreach (var leida in new[] { porId!, porEncuentro })
        {
            leida.LocalCode.Should().Be("CMP", "el código del panel tiene que sobrevivir la lectura");
            leida.LoincCode.Should().BeNull("`CMP` no es LOINC y no puede volver como si lo fuera");
        }
    }

    [Fact]
    public async Task PromoverUnaOrden_noLeCambiaLaFechaDeEmision()
    {
        // `authoredOn` se fijaba en `UtcNow` también al actualizar, así que promover la orden de
        // `draft` a `active` le corría la fecha de emisión a la hora de la promoción. Cualquier
        // medición de tiempo de respuesta del laboratorio arranca el reloj ahí: con la fecha
        // pisada, el tiempo que la orden esperó antes de emitirse desaparece de los números.
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";

        var create = await client.PostAsJsonAsync("/api/lab-orders", new
        {
            patientFhirId = "pat-fecha-1",
            encounterFhirId = enc,
            status = "draft",
            localCode = "CBC",
            localSystem = "http://imed.local/panel",
            codeText = "Hemograma completo",
            requisition = enc,
        });
        var creada = await create.Content.ReadFromJsonAsync<LabOrderDto>();
        var emitida = (await (await client.GetAsync($"/api/lab-orders/{creada!.Id}"))
            .Content.ReadFromJsonAsync<LabOrderDto>())!.AuthoredOn;

        emitida.Should().NotBeNull("el gateway sella la fecha al crear");

        // Promoción: el cliente manda el recurso sin `authoredOn`, igual que al cerrar la consulta.
        var promote = await client.PutAsJsonAsync($"/api/lab-orders/{creada.Id}", new
        {
            patientFhirId = "pat-fecha-1",
            encounterFhirId = enc,
            status = "active",
            localCode = "CBC",
            localSystem = "http://imed.local/panel",
            codeText = "Hemograma completo",
            requisition = enc,
        });
        promote.StatusCode.Should().Be(HttpStatusCode.OK);

        var despues = (await (await client.GetAsync($"/api/lab-orders/{creada.Id}"))
            .Content.ReadFromJsonAsync<LabOrderDto>())!;

        despues.Status.Should().Be("active");
        despues.AuthoredOn.Should().Be(emitida, "la orden se emitió cuando se emitió, no cuando se promovió");
    }

    [Fact]
    public async Task LaOrdenDeUnPanelPropio_seInformaComoRealizableEnLaClinica()
    {
        // Camino de lectura por id: clasifica la disponibilidad por su cuenta y era el que
        // quedaba mirando solo `LoincCode`. Con un panel propio —sin LOINC— reportaba
        // "send-out" y el médico veía que la clínica no hace un examen que sí hace.
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var orgId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(orgId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: ["org_admin"]);
        var client = fixture.CreateClientWithToken(token);

        var enc = $"enc-{Guid.NewGuid():N}";
        var create = await client.PostAsJsonAsync("/api/lab-orders", new
        {
            patientFhirId = "pat-disp-1",
            encounterFhirId = enc,
            status = "active",
            localCode = "CBC",
            localSystem = "http://imed.local/panel",
            codeText = "Hemograma completo",
            requisition = enc,
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var creada = await create.Content.ReadFromJsonAsync<LabOrderDto>();

        var porId = await (await client.GetAsync($"/api/lab-orders/{creada!.Id}"))
            .Content.ReadFromJsonAsync<LabOrderDto>();

        porId!.Availability.Should().Be("in-house",
            "CBC está en el catálogo de la org y sus pruebas están ofertadas");
    }

    [Fact]
    public async Task ElCorrelativoDeLaTarde_llevaLaFechaDeHoy_noLaDeManana()
    {
        // Prueba de cableado: `ClinicClockTests` ya fija la semántica del reloj; esto verifica que
        // el handler lo use de verdad. Sin ella, inyectar el reloj y seguir calculando con `UtcNow`
        // compilaría igual.
        //
        // 2026-08-17T02:00Z son las 20:00 del 16 en Tegucigalpa.
        await Task.Delay(500);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: Guid.NewGuid().ToString(), realmRoles: ["org_admin"]);

        using var host = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddSingleton<TimeProvider>(
                    new RelojFijo(new DateTimeOffset(2026, 8, 17, 2, 0, 0, TimeSpan.Zero)))));

        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await client.PostAsJsonAsync("/api/lab-orders/allocate-number", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var numero = (await resp.Content.ReadFromJsonAsync<AllocatedNumber>())!.Requisition;

        numero.Should().StartWith("ORD-20260816-",
            "a las 20:00 del 16 en Honduras todavía es el 16, y ese número va en el papel del paciente");
    }

    private sealed record AllocatedNumber(string Requisition);

    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }

    [Fact]
    public async Task SinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/lab-orders", new { patientFhirId = "p", status = "draft", codeText = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
