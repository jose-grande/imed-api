using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabReception;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// Tests de recepción + worklist (Épica LIS-3, F6). Usan el catálogo global
/// sembrado (GLU 2345-7 sin ayuno, GLU_FAST 1558-6 con ayuno 8h). El HapiMock
/// stubea ServiceRequest/Specimen/Provenance/Practitioner.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabReceptionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    private static async Task<string> CreateExternalOrderAsync(
        HttpClient client, string patientFhirId, string? loinc, string codeText, string priority = "routine")
    {
        var resp = await client.PostAsJsonAsync("/api/lab/reception/external-order", new
        {
            patientFhirId,
            requesterName = "Dr. Externo",
            requesterIdentifier = (string?)null,
            loincCode = loinc,
            codeText,
            priority,
            occurrenceDateTime = (string?)null,
            reasonText = (string?)null,
            note = (string?)null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<CreatedExternalOrder>();
        return created!.ServiceRequestId;
    }

    private static object ReceiveBody(string serviceRequestId, bool? fastingSatisfied = null) => new
    {
        serviceRequestId,
        fastingSatisfied,
        isWalkIn = true,
        scheduledFor = (DateTimeOffset?)null,
        typeText = (string?)null,
        collectedDateTime = (string?)null,
        note = (string?)null,
    };

    [Fact]
    public async Task Reception_sinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/lab/reception/order/sr-x");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task InHouse_recibeYGeneraAccessionYEspecimen()
    {
        var client = await AuthedClient();
        var srId = await CreateExternalOrderAsync(client, "pat-1", "2345-7", "Glucosa");

        var view = await (await client.GetAsync($"/api/lab/reception/order/{srId}")).Content.ReadFromJsonAsync<ReceptionOrderView>();
        view!.Routing.Should().Be("InHouse", "GLU está en el catálogo y ofrecida");
        view.FastingHoursMin.Should().BeNull();

        var recResp = await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody(srId));
        recResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await recResp.Content.ReadFromJsonAsync<ReceiveResult>();
        result!.Status.Should().Be("Received");
        result.Routing.Should().Be("InHouse");
        result.AccessionNumber.Should().NotBeNullOrEmpty();
        result.SpecimenFhirId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UnaPruebaSuelta_seResuelvePorElCodigoDeLaClinica_noSoloPorLoinc()
    {
        // Los paneles resolvían por código local y las pruebas sueltas no. La asimetría queda
        // tapada mientras toda prueba del catálogo tenga LOINC —hoy lo tienen las 222 sembradas—,
        // así que la prueba ordena por el código propio de la clínica, que es la forma que sí
        // distingue los dos caminos. Cuando entre un catálogo con pruebas sin LOINC, esa orden
        // habría terminado referida a un laboratorio externo.
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest(
            "pat-suelta-1", loinc: null, "Glucosa", encounter: enc, requisition: enc,
            localCode: "GLU", localSystem: "http://imed.local/test");

        var view = await (await client.GetAsync($"/api/lab/reception/order/{srId}"))
            .Content.ReadFromJsonAsync<ReceptionOrderView>();

        view!.Routing.Should().Be("InHouse", "GLU es el código de una prueba que la clínica sí hace");
    }

    [Fact]
    public async Task UnPanelPropio_seRecibeEnLaClinica_yExigeSuAyuno()
    {
        // Resolver una orden devolvía "la prueba" de la orden, y un panel no tiene una: caía en
        // send-out —referir a externo un panel que la clínica sí hace— y, peor, se quedaba sin
        // horas de ayuno, así que la precondición no se preguntaba y la muestra se recibía igual.
        // Una glucosa tomada sin ayuno no se detecta después: el resultado sale, y sale mal.
        // LIPID a propósito: es código de panel y **no** de prueba. `GLU_FAST` sirve para las dos
        // cosas, así que una orden con ese código resuelve como prueba suelta y tapa el caso.
        var client = await AuthedClient();
        var enc = $"enc-{Guid.NewGuid():N}";
        var srId = fixture.Hapi.SeedServiceRequest(
            "pat-panel-ayuno", loinc: null, "Perfil lipídico", encounter: enc, requisition: enc,
            localCode: "LIPID", localSystem: "http://imed.local/panel");

        var view = await (await client.GetAsync($"/api/lab/reception/order/{srId}"))
            .Content.ReadFromJsonAsync<ReceptionOrderView>();

        view!.Routing.Should().Be("InHouse", "el panel está en el catálogo de la sede");
        // El perfil lipídico se especifica como un rango: 9 a 12 h. Con un solo valor había
        // que elegir, y en 12 se rechazaba a quien llegaba con 10 h de ayuno válidas.
        view.FastingHoursMin.Should().Be(9, "manda el mayor de los mínimos del panel");
        view.FastingHoursMax.Should().Be(12, "y el menor de los máximos");

        var result = await (await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody(srId, fastingSatisfied: false)))
            .Content.ReadFromJsonAsync<ReceiveResult>();

        result!.Status.Should().Be("Blocked");
        result.SpecimenFhirId.Should().BeNull("sin ayuno no se toma la muestra");
    }

    [Fact]
    public async Task SendOut_noGeneraEspecimen()
    {
        var client = await AuthedClient();
        var srId = await CreateExternalOrderAsync(client, "pat-2", "99999-9", "Examen no ofrecido");

        var view = await (await client.GetAsync($"/api/lab/reception/order/{srId}")).Content.ReadFromJsonAsync<ReceptionOrderView>();
        view!.Routing.Should().Be("SendOut", "el LOINC no está en el catálogo de la sede");

        var result = await (await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody(srId)))
            .Content.ReadFromJsonAsync<ReceiveResult>();
        result!.Routing.Should().Be("SendOut");
        result.AccessionNumber.Should().BeNull();
        result.SpecimenFhirId.Should().BeNull();
    }

    [Fact]
    public async Task AyunoNoCumplido_bloqueaSinCrearMuestra()
    {
        var client = await AuthedClient();
        var srId = await CreateExternalOrderAsync(client, "pat-3", "1558-6", "Glucosa en ayunas");

        var view = await (await client.GetAsync($"/api/lab/reception/order/{srId}")).Content.ReadFromJsonAsync<ReceptionOrderView>();
        view!.FastingHoursMin.Should().Be(8, "GLU_FAST requiere al menos 8 h");
        view.FastingHoursMax.Should().BeNull("una glucosa no tiene cota superior de ayuno");

        var result = await (await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody(srId, fastingSatisfied: false)))
            .Content.ReadFromJsonAsync<ReceiveResult>();
        result!.Status.Should().Be("Blocked");
        result.BlockReason.Should().NotBeNullOrEmpty();
        result.SpecimenFhirId.Should().BeNull();
    }

    [Fact]
    public async Task Worklist_muestraLaOrdenRecibida()
    {
        var client = await AuthedClient();
        var srId = await CreateExternalOrderAsync(client, "pat-4", "2345-7", "Glucosa", priority: "stat");
        var result = await (await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody(srId)))
            .Content.ReadFromJsonAsync<ReceiveResult>();

        var items = await (await client.GetAsync("/api/lab/worklist")).Content.ReadFromJsonAsync<WorklistItem[]>();
        items!.Should().Contain(i => i.AccessionNumber == result!.AccessionNumber && i.Priority == "Stat");
    }

    [Fact]
    public async Task Receive_conRolInsuficiente_retorna403()
    {
        var client = await AuthedClient("practitioner"); // LabRead pero no LabTechnician
        var resp = await client.PostAsJsonAsync("/api/lab/reception/receive", ReceiveBody("sr-x"));
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Worklist_sinRolDeLab_retorna403()
    {
        var client = await AuthedClient("auxiliar"); // fuera de LabRead
        var resp = await client.GetAsync("/api/lab/worklist");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
