using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.LabCatalog;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.LabCatalog;

/// <summary>
/// Tests del catálogo de laboratorio (Épica LIS-1, L1-F5). El catálogo global
/// (mock HN) lo siembra <c>LabCatalogSeeder</c> al arrancar el host; no se trunca
/// en <c>ResetStateAsync</c>, así que los globales están disponibles. Las
/// escrituras se aíslan por organización (token con org aleatoria por test).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LabCatalogTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Por defecto org_admin (cubre LabRead + LabSupervisor). Pasar roles explícitos
    // para probar el RBAC fino de LIS-2.
    private async Task<HttpClient> AuthedClient(params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    // ------------------------------- Pruebas -------------------------------

    [Fact]
    public async Task SearchTests_sinAuth_retorna401()
    {
        await Task.Delay(300);
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/lab-catalog/tests");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SearchTests_traeGlobalesSeed()
    {
        var client = await AuthedClient();
        var resp = await client.GetAsync("/api/lab-catalog/tests?q=glucosa");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var tests = await resp.Content.ReadFromJsonAsync<LabTestDto[]>();
        tests!.Should().Contain(t => t.Code == "GLU", "el seed mock incluye Glucosa");
        tests!.Should().Contain(t => t.LoincCode == "2345-7");
    }

    [Fact]
    public async Task CreateTest_apareceEnElCatalogo()
    {
        var client = await AuthedClient();
        var body = new
        {
            code = "CUSTOM1",
            name = "Prueba personalizada",
            datatype = "Numeric",
            loincCode = (string?)null,
            unit = "mg/dL",
            department = "Chemistry",
            fastingHours = (int?)null,
            collectionType = "blood",
            patientInstruction = (string?)null,
        };
        var create = await client.PostAsJsonAsync("/api/lab-catalog/tests", body);
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<LabTestDto>();
        created!.Code.Should().Be("CUSTOM1");
        created.OrganizationId.Should().NotBeNull("la prueba se crea con scope de la org del token");

        var list = await client.GetAsync("/api/lab-catalog/tests?q=personalizada");
        var tests = await list.Content.ReadFromJsonAsync<LabTestDto[]>();
        tests!.Should().Contain(t => t.Id == created.Id);
    }

    [Fact]
    public async Task CreateTest_datatypeInvalido_retorna400()
    {
        var client = await AuthedClient();
        var body = new { code = "BAD1", name = "X", datatype = "Floaty", department = "Chemistry" };
        var resp = await client.PostAsJsonAsync("/api/lab-catalog/tests", body);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateTest_codigoDuplicadoEnMismaOrg_retorna409()
    {
        var client = await AuthedClient();
        var body = new { code = "DUP1", name = "Una", datatype = "Numeric", department = "Chemistry" };
        (await client.PostAsJsonAsync("/api/lab-catalog/tests", body)).StatusCode.Should().Be(HttpStatusCode.Created);
        var second = await client.PostAsJsonAsync("/api/lab-catalog/tests", body);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task SetTestActive_desactivaYReactiva()
    {
        var client = await AuthedClient();
        var body = new { code = "TOG1", name = "Toggle", datatype = "Numeric", department = "Chemistry" };
        var created = await (await client.PostAsJsonAsync("/api/lab-catalog/tests", body)).Content.ReadFromJsonAsync<LabTestDto>();

        var off = await client.PostAsJsonAsync($"/api/lab-catalog/tests/{created!.Id}/active", new { active = false });
        var offDto = await off.Content.ReadFromJsonAsync<LabTestDto>();
        offDto!.Active.Should().BeFalse();

        var on = await client.PostAsJsonAsync($"/api/lab-catalog/tests/{created.Id}/active", new { active = true });
        (await on.Content.ReadFromJsonAsync<LabTestDto>())!.Active.Should().BeTrue();
    }

    // ------------------------------- Paneles -------------------------------

    [Fact]
    public async Task ListPanels_traeSeed()
    {
        var client = await AuthedClient();
        var panels = await (await client.GetAsync("/api/lab-catalog/panels")).Content.ReadFromJsonAsync<LabPanelDto[]>();
        panels!.Should().Contain(p => p.Code == "CBC");
        panels!.Should().Contain(p => p.Code == "CMP");
    }

    [Fact]
    public async Task GetPanelDetail_CMP_traeComposicionOrdenada()
    {
        var client = await AuthedClient();
        var panels = await (await client.GetAsync("/api/lab-catalog/panels")).Content.ReadFromJsonAsync<LabPanelDto[]>();
        var cmp = panels!.First(p => p.Code == "CMP");

        var detail = await (await client.GetAsync($"/api/lab-catalog/panels/{cmp.Id}")).Content.ReadFromJsonAsync<LabPanelDetailDto>();
        detail!.ActiveVersion.Should().Be(1);
        detail.Items.Should().HaveCount(14, "CMP = BMP(8) + 6 hepáticas/proteínas");
        detail.Items[0].DisplayOrder.Should().Be(0);
        detail.Items.Should().Contain(i => i.Code == "ALT");
    }

    [Fact]
    public async Task CreatePanel_yNuevaVersion_reemplazaLaActiva()
    {
        var client = await AuthedClient();

        // Dos pruebas para el panel.
        var t1 = await (await client.PostAsJsonAsync("/api/lab-catalog/tests",
            new { code = "PT1", name = "P1", datatype = "Numeric", department = "Chemistry" })).Content.ReadFromJsonAsync<LabTestDto>();
        var t2 = await (await client.PostAsJsonAsync("/api/lab-catalog/tests",
            new { code = "PT2", name = "P2", datatype = "Numeric", department = "Chemistry" })).Content.ReadFromJsonAsync<LabTestDto>();

        var create = await client.PostAsJsonAsync("/api/lab-catalog/panels",
            new { code = "MYPANEL", name = "Mi panel", description = (string?)null, testIds = new[] { t1!.Id, t2!.Id } });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var v1 = await create.Content.ReadFromJsonAsync<LabPanelDetailDto>();
        v1!.ActiveVersion.Should().Be(1);
        v1.Items.Should().HaveCount(2);

        // Nueva versión con solo una prueba → v2 activa.
        var v2resp = await client.PostAsJsonAsync($"/api/lab-catalog/panels/{v1.Id}/versions", new { testIds = new[] { t1.Id } });
        v2resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v2 = await v2resp.Content.ReadFromJsonAsync<LabPanelDetailDto>();
        v2!.ActiveVersion.Should().Be(2);
        v2.Items.Should().HaveCount(1);

        // El detalle ahora refleja v2.
        var detail = await (await client.GetAsync($"/api/lab-catalog/panels/{v1.Id}")).Content.ReadFromJsonAsync<LabPanelDetailDto>();
        detail!.ActiveVersion.Should().Be(2);
        detail.Items.Should().HaveCount(1);
    }

    // -------------------------- Rangos de referencia -----------------------

    [Fact]
    public async Task ListRanges_dePotasioGlobal_traeValoresCriticos()
    {
        var client = await AuthedClient();
        var tests = await (await client.GetAsync("/api/lab-catalog/tests?q=potasio")).Content.ReadFromJsonAsync<LabTestDto[]>();
        var k = tests!.First(t => t.Code == "K");

        var ranges = await (await client.GetAsync($"/api/lab-catalog/reference-ranges?testId={k.Id}")).Content.ReadFromJsonAsync<ReferenceRangeDto[]>();
        ranges!.Should().Contain(r => r.CriticalHigh == 6.5m && r.CriticalLow == 2.5m, "el seed define K crítico 2.5–6.5");
    }

    [Fact]
    public async Task CreateRange_paraPruebaNueva_apareceEnLaLista()
    {
        var client = await AuthedClient();
        var test = await (await client.PostAsJsonAsync("/api/lab-catalog/tests",
            new { code = "RNG1", name = "Con rango", datatype = "Numeric", unit = "mg/dL", department = "Chemistry" }))
            .Content.ReadFromJsonAsync<LabTestDto>();

        var body = new
        {
            testId = test!.Id, sex = "Any", ageMinYears = 18, ageMaxYears = (int?)null, pregnancy = (bool?)null,
            method = (string?)null, low = 1.0m, high = 9.0m, criticalLow = 0.5m, criticalHigh = 15.0m, unit = "mg/dL",
        };
        var create = await client.PostAsJsonAsync("/api/lab-catalog/reference-ranges", body);
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var ranges = await (await client.GetAsync($"/api/lab-catalog/reference-ranges?testId={test.Id}")).Content.ReadFromJsonAsync<ReferenceRangeDto[]>();
        ranges!.Should().ContainSingle(r => r.High == 9.0m && r.CriticalHigh == 15.0m);
    }

    // ----------------------- Fórmulas calculadas / TAT ---------------------

    [Fact]
    public async Task ListDerivedYTat_traenSeed()
    {
        var client = await AuthedClient();
        var derived = await (await client.GetAsync("/api/lab-catalog/derived-calculations")).Content.ReadFromJsonAsync<DerivedCalculationDto[]>();
        derived!.Should().Contain(d => d.OutputTestCode == "LDL_CALC");

        var tat = await (await client.GetAsync("/api/lab-catalog/tat-targets")).Content.ReadFromJsonAsync<TatTargetDto[]>();
        tat!.Should().Contain(t => t.TargetCode == "CBC");
    }

    // ------------------------------- RBAC (LIS-2) -------------------------------

    [Fact]
    public async Task Read_conRolDeLab_permitido()
    {
        var client = await AuthedClient("lab_tech"); // LabRead, no LabSupervisor
        var resp = await client.GetAsync("/api/lab-catalog/tests?q=glucosa");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Read_sinRolDeLab_retorna403()
    {
        var client = await AuthedClient("auxiliar"); // autenticado pero fuera de LabRead
        var resp = await client.GetAsync("/api/lab-catalog/tests");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Write_conRolInsuficiente_retorna403()
    {
        var client = await AuthedClient("practitioner"); // LabRead pero NO LabSupervisor
        var resp = await client.PostAsJsonAsync("/api/lab-catalog/tests",
            new { code = "NOPE1", name = "X", datatype = "Numeric", department = "Chemistry" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Write_conSupervisor_permitido()
    {
        var client = await AuthedClient("lab_supervisor");
        var resp = await client.PostAsJsonAsync("/api/lab-catalog/tests",
            new { code = "SUP1", name = "Permitido", datatype = "Numeric", department = "Chemistry" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
