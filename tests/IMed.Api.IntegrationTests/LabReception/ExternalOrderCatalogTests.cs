using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabCatalog;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabReception;

/// <summary>
/// La "Nueva orden externa" de recepción solo admite pruebas y paneles parametrizados que el
/// laboratorio hace; lo demás se refiere afuera. Observación de staging: el buscador ofrecía todo
/// LOINC, y una orden con una prueba fuera del catálogo llegaba sin muestra, tubo ni rangos.
///
/// <para>La prueba de punta a punta cubre lo que hace valer la restricción: la orden lleva a la
/// prueba elegida, y al ingresar resultados se aplican sus rangos — también si no tiene LOINC.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class ExternalOrderCatalogTests(ImedApiFixture fixture) : IAsyncLifetime
{
    private const string TestSystem = "http://imed.local/lab-test";

    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"]));
    }

    private static Task<HttpResponseMessage> OrdenAsync(HttpClient client, params object[] exams) =>
        client.PostAsJsonAsync("/api/lab/reception/external-order-multi", new
        {
            patientFhirId = "pat-cat-1",
            requesterName = "Dr. Externo",
            requesterIdentifier = (string?)null,
            requesterInstitution = (string?)null,
            priority = "routine",
            occurrenceDateTime = (string?)null,
            reasonText = (string?)null,
            note = (string?)null,
            exams,
        });

    private static object Prueba(string? loinc, string? code, string name) => new
    {
        loincCode = loinc, localCode = code, localSystem = code is null ? null : TestSystem,
        codeText = name, codeDisplay = name,
    };

    private static async Task<LabTestDto> PruebaDelCatalogoAsync(HttpClient client, string term) =>
        (await client.GetFromJsonAsync<List<LabTestDto>>($"/api/lab-catalog/tests?q={term}&activeOnly=true"))!
            .First();

    [Fact]
    public async Task PruebaDelCatalogo_seAcepta()
    {
        var client = await AuthedClient();
        var glu = await PruebaDelCatalogoAsync(client, "Glucosa");

        (await OrdenAsync(client, Prueba(glu.LoincCode, glu.Code, glu.Name)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task LoincFueraDelCatalogo_seRechaza()
    {
        var client = await AuthedClient();

        // Un LOINC real que no está parametrizado: antes se aceptaba y la orden llegaba a
        // recepción como "no está en el catálogo", sin muestra ni rangos.
        var resp = await OrdenAsync(client, Prueba("5196-1", null, "Antígeno de superficie de hepatitis B (LOINC suelto)"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("no está en el catálogo");
    }

    [Fact]
    public async Task TextoLibre_seRechaza()
    {
        var client = await AuthedClient();

        (await OrdenAsync(client, Prueba(null, null, "Perfil que no existe")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PruebaQueSeRefiereAfuera_noSeOfreceNiSeAcepta()
    {
        var client = await AuthedClient();
        var glu = await PruebaDelCatalogoAsync(client, "Glucosa");
        (await client.PutAsJsonAsync("/api/lab-catalog/offerings", new { testId = glu.Id, offered = false }))
            .IsSuccessStatusCode.Should().BeTrue();

        var buscables = await client.GetFromJsonAsync<List<LabTestDto>>("/api/lab-catalog/tests?q=Glucosa&orderable=true");
        buscables.Should().NotContain(t => t.Id == glu.Id, "el laboratorio no la hace: se refiere afuera");

        var resp = await OrdenAsync(client, Prueba(glu.LoincCode, glu.Code, glu.Name));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("referirla afuera");
    }

    [Fact]
    public async Task BuscadorDeRecepcion_encuentraPorLoinc()
    {
        var client = await AuthedClient();

        var porLoinc = await client.GetFromJsonAsync<List<LabTestDto>>("/api/lab-catalog/tests?q=2345-7&orderable=true");

        porLoinc.Should().ContainSingle(t => t.LoincCode == "2345-7");
    }

    [Fact]
    public async Task PruebaSinLoinc_seOrdenaYAlIngresarResultadosAplicaSusRangos()
    {
        var client = await AuthedClient();

        // Una prueba propia del cliente, sin LOINC: el catálogo lo permite.
        var creada = await client.PostAsJsonAsync("/api/lab-catalog/tests", new
        {
            code = "XPROP", name = "Prueba propia", datatype = "Numeric", loincCode = (string?)null, unit = "U/L",
            department = "Chemistry", fastingHoursMin = (int?)null, fastingHoursMax = (int?)null,
            collectionType = (string?)null, patientInstruction = (string?)null,
            specimenTypeId = (Guid?)null, tubeTypeId = (Guid?)null, collectionMode = (string?)null,
            minVolumeMl = (decimal?)null, preferredVolumeMl = (decimal?)null, analyzerMinVolumeMl = (decimal?)null,
            stabilityMinutes = (int?)null,
        });
        creada.IsSuccessStatusCode.Should().BeTrue(await creada.Content.ReadAsStringAsync());
        var prueba = (await creada.Content.ReadFromJsonAsync<LabTestDto>())!;
        (await client.PostAsJsonAsync("/api/lab-catalog/reference-ranges", new
        {
            testId = prueba.Id, sex = "Any", ageMinYears = (int?)null, ageMaxYears = (int?)null,
            pregnancy = (bool?)null, method = (string?)null,
            low = 10m, high = 40m, criticalLow = (decimal?)null, criticalHigh = (decimal?)null, unit = "U/L",
        })).IsSuccessStatusCode.Should().BeTrue();

        var orden = await OrdenAsync(client, Prueba(null, prueba.Code, prueba.Name));
        orden.StatusCode.Should().Be(HttpStatusCode.Created, await orden.Content.ReadAsStringAsync());
        var srId = (await orden.Content.ReadFromJsonAsync<CreatedExternalOrderMulti>())!.ServiceRequestIds.Single();

        (await (await client.PostAsJsonAsync("/api/lab/reception/receive", new
        {
            serviceRequestId = srId, fastingSatisfied = (bool?)null, isWalkIn = true,
            scheduledFor = (DateTimeOffset?)null, typeText = (string?)null, collectedDateTime = (string?)null, note = (string?)null,
        })).Content.ReadFromJsonAsync<ReceiveResult>())!.Status.Should().Be("Received");

        // La hoja de captura resuelve la orden a la prueba elegida, con su rango.
        var hoja = await client.GetFromJsonAsync<ResultEntrySheetDto>($"/api/lab/results/entry-sheet/{srId}");
        var analito = hoja!.Analytes.Should().ContainSingle().Subject;
        analito.LabTestId.Should().Be(prueba.Id);
        analito.ReferenceHigh.Should().Be(40m);

        // Al guardar, el rango se aplica por la prueba y no por LOINC (que no tiene).
        var guardado = await client.PostAsJsonAsync("/api/lab/results", new
        {
            serviceRequestId = srId,
            externalLabName = (string?)null,
            results = new[]
            {
                new { loinc = (string?)null, codeText = prueba.Name, valueNumeric = 55m, valueString = (string?)null, unit = "U/L", labTestId = prueba.Id },
            },
            conclusion = (string?)null,
        });
        guardado.StatusCode.Should().Be(HttpStatusCode.OK, await guardado.Content.ReadAsStringAsync());
        (await guardado.Content.ReadFromJsonAsync<EnterResultsResult>())!
            .Observations.Should().ContainSingle().Which.FlagCode.Should().Be("H", "55 está sobre el máximo 40");
    }
}
