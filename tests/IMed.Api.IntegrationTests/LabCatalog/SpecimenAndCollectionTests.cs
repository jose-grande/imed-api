using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabCatalog;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.LabCatalog;

/// <summary>
/// Tipo de muestra (qué material se analiza) y tipo de recolección (cómo se obtiene).
/// Observación de staging: la recolección era texto libre, y faltaban Suero y Plasma; "Sangre"
/// debía decir "Sangre total".
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class SpecimenAndCollectionTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HttpClient Client, Guid OrgId)> AuthedClient()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        var organizationId = Guid.NewGuid();
        await fixture.ProvisionLabCatalogAsync(organizationId);
        fixture.Keycloak.RegisterUser(userId);
        return (fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: organizationId.ToString(), realmRoles: ["org_admin"])), organizationId);
    }

    private static object NuevaPrueba(string code, string? collectionType) => new
    {
        code, name = $"Prueba {code}", datatype = "Numeric", loincCode = (string?)null, unit = "mg/dL",
        department = "Chemistry", fastingHoursMin = (int?)null, fastingHoursMax = (int?)null,
        collectionType, patientInstruction = (string?)null,
        specimenTypeId = (Guid?)null, tubeTypeId = (Guid?)null, collectionMode = (string?)null,
        minVolumeMl = (decimal?)null, preferredVolumeMl = (decimal?)null, analyzerMinVolumeMl = (decimal?)null,
        stabilityMinutes = (int?)null,
    };

    private static object Edicion(LabTestDto t, string? collectionType) => new
    {
        name = t.Name, datatype = t.Datatype, loincCode = t.LoincCode, unit = t.Unit, department = t.Department,
        fastingHoursMin = t.FastingHoursMin, fastingHoursMax = t.FastingHoursMax,
        collectionType, patientInstruction = t.PatientInstruction,
        specimenTypeId = t.SpecimenTypeId, tubeTypeId = t.TubeTypeId, collectionMode = t.CollectionMode,
        minVolumeMl = (decimal?)null, preferredVolumeMl = (decimal?)null, analyzerMinVolumeMl = (decimal?)null,
        stabilityMinutes = (int?)null,
    };

    [Fact]
    public async Task TiposDeMuestra_incluyenSangreTotalSueroYPlasma()
    {
        var (client, _) = await AuthedClient();

        var tipos = await client.GetFromJsonAsync<List<SpecimenTypeDto>>("/api/lab-catalog/specimen-types");

        tipos!.Select(t => t.Name).Should().Contain(
            ["Sangre total", "Suero", "Plasma", "Orina", "Orina de 24 horas", "Heces", "Esputo", "Secreciones",
             "Líquido cefalorraquídeo (LCR)"]);
        tipos.Should().NotContain(t => t.Name == "Sangre");
    }

    [Fact]
    public async Task PruebasSembradas_hemogramaEnSangreTotal_quimicaEnSuero_conRecoleccionDeLaLista()
    {
        var (client, _) = await AuthedClient();
        var tipos = (await client.GetFromJsonAsync<List<SpecimenTypeDto>>("/api/lab-catalog/specimen-types"))!
            .ToDictionary(t => t.Id, t => t.Code);
        var pruebas = (await client.GetFromJsonAsync<List<LabTestDto>>("/api/lab-catalog/tests?activeOnly=true&limit=200"))!;

        var hemoglobina = pruebas.Single(t => t.Code == "HGB");
        tipos[hemoglobina.SpecimenTypeId!.Value].Should().Be("BLOOD");
        hemoglobina.CollectionType.Should().Be("Venopunción");

        var glucosa = pruebas.Single(t => t.Code == "GLU");
        tipos[glucosa.SpecimenTypeId!.Value].Should().Be("SERUM");
        glucosa.CollectionType.Should().Be("Venopunción");

        pruebas.Single(t => t.Code == "UA_PH").CollectionType.Should().Be("Micción espontánea");
    }

    [Fact]
    public async Task TiposDeRecoleccion_seSirvenComoLista()
    {
        var (client, _) = await AuthedClient();

        var lista = await client.GetFromJsonAsync<List<string>>("/api/lab-catalog/collection-methods");

        lista.Should().Contain(["Venopunción", "Punción capilar", "Micción espontánea",
            "Recolección de orina de 24 horas", "Recolección por sonda", "Hisopado", "Aspiración"]);
    }

    [Fact]
    public async Task CrearPrueba_recoleccionFueraDeLaLista_400_yDeLaLista_seGuardaNormalizada()
    {
        var (client, _) = await AuthedClient();

        (await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XREC1", "blood")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var ok = await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XREC2", "venopunción"));
        ok.IsSuccessStatusCode.Should().BeTrue(await ok.Content.ReadAsStringAsync());
        (await ok.Content.ReadFromJsonAsync<LabTestDto>())!.CollectionType.Should().Be("Venopunción");
    }

    [Fact]
    public async Task EditarPrueba_conValorViejoSinCambiarlo_seAcepta_peroNoSePuedePonerOtroFueraDeLista()
    {
        var (client, _) = await AuthedClient();
        var creada = (await (await client.PostAsJsonAsync("/api/lab-catalog/tests", NuevaPrueba("XREC3", null)))
            .Content.ReadFromJsonAsync<LabTestDto>())!;

        // Un texto libre de antes de la lista.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE lab_test SET collection_type = 'sangre venosa' WHERE id = {0}", creada.Id);
        }
        var vieja = (await client.GetFromJsonAsync<LabTestDto>($"/api/lab-catalog/tests/{creada.Id}"))!;
        vieja.CollectionType.Should().Be("sangre venosa");

        // Cambiar otro campo sin tocar la recolección no obliga a corregirla.
        (await client.PutAsJsonAsync($"/api/lab-catalog/tests/{creada.Id}", Edicion(vieja, "sangre venosa")))
            .IsSuccessStatusCode.Should().BeTrue();

        (await client.PutAsJsonAsync($"/api/lab-catalog/tests/{creada.Id}", Edicion(vieja, "otra cosa")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var corregida = await client.PutAsJsonAsync($"/api/lab-catalog/tests/{creada.Id}", Edicion(vieja, "Punción capilar"));
        (await corregida.Content.ReadFromJsonAsync<LabTestDto>())!.CollectionType.Should().Be("Punción capilar");
    }
}
