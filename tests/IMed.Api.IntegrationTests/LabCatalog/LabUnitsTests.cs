using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabCatalog;
using IMed.Api.Application.LabReception;
using IMed.Api.Application.LabResults;
using IMed.Api.Domain.Catalogs.Lab;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.LabCatalog;

/// <summary>
/// Unidades de laboratorio: lista fija de códigos UCUM con etiqueta. Observación de staging:
/// guardar el resultado de una TSH daba 500 porque la unidad era texto libre ("mIU/L") y HAPI la
/// rechazaba por no ser UCUM. El mock de HAPI rechaza igual que el real cualquier código que no
/// esté en <see cref="LabUnits"/>.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabUnitsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<HttpClient> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        fixture.Keycloak.RegisterUser(userId);
        return fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"]));
    }

    private static object NuevaPrueba(string code, string? unit) => new
    {
        code, name = $"Prueba {code}", datatype = "Numeric", loincCode = (string?)null, unit,
        department = "Chemistry", fastingHoursMin = (int?)null, fastingHoursMax = (int?)null,
        collectionType = (string?)null, patientInstruction = (string?)null,
        specimenTypeId = (Guid?)null, tubeTypeId = (Guid?)null, collectionMode = (string?)null,
        minVolumeMl = (decimal?)null, preferredVolumeMl = (decimal?)null, analyzerMinVolumeMl = (decimal?)null,
        stabilityMinutes = (int?)null,
    };

    private static object Edicion(LabTestDto t, string? unit) => new
    {
        name = t.Name, datatype = t.Datatype, loincCode = t.LoincCode, unit, department = t.Department,
        fastingHoursMin = t.FastingHoursMin, fastingHoursMax = t.FastingHoursMax,
        collectionType = t.CollectionType, patientInstruction = t.PatientInstruction,
        specimenTypeId = t.SpecimenTypeId, tubeTypeId = t.TubeTypeId, collectionMode = t.CollectionMode,
        minVolumeMl = (decimal?)null, preferredVolumeMl = (decimal?)null, analyzerMinVolumeMl = (decimal?)null,
        stabilityMinutes = (int?)null,
    };

    private static object NuevoRango(Guid testId, string? unit) => new
    {
        testId, sex = "Any", ageMinYears = (int?)null, ageMaxYears = (int?)null, pregnancy = (bool?)null,
        method = (string?)null, low = 1.0m, high = 9.0m, criticalLow = (decimal?)null, criticalHigh = (decimal?)null, unit,
    };

    private sealed record UnitDto(string Code, string Label, string Group);

    [Fact]
    public void Lista_codigosUnicos_yEtiquetas()
    {
        LabUnits.All.Select(u => u.Code).Should().OnlyHaveUniqueItems();
        LabUnits.All.Select(u => u.Label).Should().OnlyHaveUniqueItems();
        LabUnits.LabelFor("m[IU]/L").Should().Be("mIU/L");
        LabUnits.LabelFor("10*3/uL").Should().Be("×10³/µL");
        LabUnits.LabelFor(null).Should().BeNull();
    }

    [Theory]
    [InlineData("mIU/L")]
    [InlineData("10^3/uL")]
    [InlineData("mEq/L")]
    [InlineData("mg/dl")]
    public void Require_rechazaLoQueNoEsCodigoDeLaLista(string unidad) =>
        FluentActions.Invoking(() => LabUnits.Require(unidad, "unit")).Should().Throw<ArgumentException>();

    [Fact]
    public async Task Unidades_seSirvenComoLista()
    {
        var client = await AuthedClient();

        var lista = await client.GetFromJsonAsync<List<UnitDto>>("/api/lab-catalog/units");

        lista.Should().ContainEquivalentOf(new UnitDto("m[IU]/L", "mIU/L", "Actividad enzimática / hormonal"));
        lista!.Count.Should().Be(LabUnits.All.Count);
    }

    [Fact]
    public async Task PruebasSembradas_todasConUnidadDeLaLista_TSHenMIU()
    {
        var client = await AuthedClient();

        var pruebas = (await client.GetFromJsonAsync<List<LabTestDto>>("/api/lab-catalog/tests?activeOnly=false&limit=500"))!;

        pruebas.Should().OnlyContain(t => t.UnitAllowed);
        var tsh = pruebas.Single(t => t.Code == "TSH");
        tsh.Unit.Should().Be("m[IU]/L");
        tsh.UnitLabel.Should().Be("mIU/L");
    }

    [Fact]
    public async Task CrearPrueba_unidadFueraDeLaLista_400_yDeLaLista_devuelveEtiqueta()
    {
        var client = await AuthedClient();

        var mala = await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XU1", "mIU/L"));
        mala.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mala.Content.ReadAsStringAsync()).Should().Contain("no está en la lista de unidades admitidas");

        var ok = await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XU2", "m[IU]/L"));
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync());
        var creada = (await ok.Content.ReadFromJsonAsync<LabTestDto>())!;
        creada.Unit.Should().Be("m[IU]/L");
        creada.UnitLabel.Should().Be("mIU/L");
    }

    [Fact]
    public async Task CrearRango_unidadFueraDeLaLista_400()
    {
        var client = await AuthedClient();
        var prueba = (await (await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XU3", "mg/dL")))
            .Content.ReadFromJsonAsync<LabTestDto>())!;

        (await client.PostAsJsonAsync("/api/lab-catalog/reference-ranges", NuevoRango(prueba.Id, "mg/dl")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/lab-catalog/reference-ranges", NuevoRango(prueba.Id, "mg/dL")))
            .IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task PruebaConUnidadVieja_seMarca_yNoSeGuardaHastaElegirUnaDeLaLista()
    {
        var client = await AuthedClient();
        var creada = (await (await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XU4", "mg/dL")))
            .Content.ReadFromJsonAsync<LabTestDto>())!;

        // Un texto libre que la migración no reconoció.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("UPDATE lab_test SET unit = 'unidades' WHERE id = {0}", creada.Id);
        }
        var vieja = (await client.GetFromJsonAsync<LabTestDto>($"/api/lab-catalog/tests/{creada.Id}"))!;
        vieja.UnitAllowed.Should().BeFalse();
        vieja.UnitLabel.Should().Be("unidades");

        (await client.PutAsJsonAsync($"/api/lab-catalog/tests/{creada.Id}", Edicion(vieja, "unidades")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var corregida = await client.PutAsJsonAsync($"/api/lab-catalog/tests/{creada.Id}", Edicion(vieja, "U/L"));
        (await corregida.Content.ReadFromJsonAsync<LabTestDto>())!.UnitAllowed.Should().BeTrue();
    }

    /// <summary>Crea una orden externa de TSH y la recibe in-house; devuelve el serviceRequestId.</summary>
    private static async Task<string> ReceiveTshAsync(HttpClient client, string patientId)
    {
        var created = await (await client.PostAsJsonAsync("/api/lab/reception/external-order", new
        {
            patientFhirId = patientId, requesterName = "Dr. Externo", requesterIdentifier = (string?)null,
            loincCode = "3016-3", codeText = "TSH", priority = "routine",
            occurrenceDateTime = (string?)null, reasonText = (string?)null, note = (string?)null,
        })).Content.ReadFromJsonAsync<CreatedExternalOrder>();
        await client.PostAsJsonAsync("/api/lab/reception/receive", new
        {
            serviceRequestId = created!.ServiceRequestId, fastingSatisfied = (bool?)null, isWalkIn = true,
            scheduledFor = (DateTimeOffset?)null, typeText = (string?)null, collectedDateTime = (string?)null, note = (string?)null,
        });
        return created.ServiceRequestId;
    }

    private static object TshResult(string srId, string? unit) => new
    {
        serviceRequestId = srId,
        externalLabName = (string?)null,
        results = new[] { new { loinc = "3016-3", codeText = "TSH", valueNumeric = 2.1m, valueString = (string?)null, unit } },
        conclusion = (string?)null,
    };

    [Fact]
    public async Task GuardarResultadoTSH_seGuardaConEtiquetaYCodigoUCUM()
    {
        var client = await AuthedClient();
        var srId = await ReceiveTshAsync(client, "pat-tsh");

        var hoja = (await client.GetFromJsonAsync<ResultEntrySheetDto>($"/api/lab/results/entry-sheet/{srId}"))!;
        var analito = hoja.Analytes.Single();
        analito.Unit.Should().Be("m[IU]/L");
        analito.UnitLabel.Should().Be("mIU/L");

        var resp = await client.PostAsJsonAsync("/api/lab/results", TshResult(srId, analito.Unit));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var obsId = (await resp.Content.ReadFromJsonAsync<EnterResultsResult>())!.Observations.Single().ObservationId;

        fixture.Hapi.ObservationUnit(obsId).Should().Be(("mIU/L", "m[IU]/L"));
    }

    [Fact]
    public async Task GuardarResultado_unidadFueraDeLaLista_400ConMensaje_yNoDejaNadaEscrito()
    {
        var client = await AuthedClient();
        var srId = await ReceiveTshAsync(client, "pat-tsh-mala");

        var mala = await client.PostAsJsonAsync("/api/lab/results", TshResult(srId, "mIU/L"));
        mala.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await mala.Content.ReadAsStringAsync()).Should().Contain("Corregila en el catálogo");

        // Nada quedó anclado: el guardado correcto pasa (no hay informe previo que lo bloquee).
        (await client.PostAsJsonAsync("/api/lab/results", TshResult(srId, "m[IU]/L")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
