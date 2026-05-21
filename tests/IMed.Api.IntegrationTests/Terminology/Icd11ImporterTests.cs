using FluentAssertions;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Terminology;

/// <summary>
/// Tests del importer ICD-11 verificando el seed que produce el fixture
/// <c>Fixtures/icd11-test-tabulation.txt</c>: filtros, strip de prefijo visual,
/// derivación de parent-code, mapping a Properties.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class Icd11ImporterTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<Icd11Code>> GetAllAsync()
    {
        // Esperar a que el HostedService seeder termine de cargar el fixture.
        await Task.Delay(800);

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Icd11Catalog.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Seed_skipeaChaptersYBlocks_soloImportaCategories()
    {
        var rows = await GetAllAsync();
        // Fixture: 1 chapter + 1 block + 18 categories. Esperamos exactamente 18.
        rows.Should().HaveCount(18, "se filtran chapter+block; sólo categories con code se persisten");
    }

    [Fact]
    public async Task Seed_strippeaPrefijoVisualDelTitulo()
    {
        var rows = await GetAllAsync();
        var cholera = rows.Should().ContainSingle(x => x.Code == "1A00").Subject;

        // El TSV tiene "- - - Cólera" — el prefijo visual debe quedar fuera.
        cholera.DisplayEs.Should().Be("Cólera");
        cholera.DisplayEn.Should().Be("Cholera");

        // Misma lógica para uno de profundidad mayor (4 niveles → "- - - - ...").
        var enteropatogenic = rows.Should().ContainSingle(x => x.Code == "1A03.0").Subject;
        enteropatogenic.DisplayEs.Should().Be("Infección por Escherichia coli enteropatógena");
    }

    [Fact]
    public async Task Seed_derivaParentCodePorConvencionDePrefijos()
    {
        var rows = await GetAllAsync();

        // 1A03.0 → parent = 1A03
        var dotted = rows.Should().ContainSingle(x => x.Code == "1A03.0").Subject;
        dotted.Properties.Should().ContainKey("parent-code").WhoseValue.Should().Be("1A03");

        // 5A11.0 → parent = 5A11
        var diabKeto = rows.Should().ContainSingle(x => x.Code == "5A11.0").Subject;
        diabKeto.Properties["parent-code"].Should().Be("5A11");

        // 1A00 (sin punto) → no debe tener parent-code (top-level del block).
        var cholera = rows.Should().ContainSingle(x => x.Code == "1A00").Subject;
        cholera.Properties.Should().NotContainKey("parent-code");
    }

    [Fact]
    public async Task Seed_pueblaPropertiesEsperadas()
    {
        var rows = await GetAllAsync();
        var cholera = rows.Should().ContainSingle(x => x.Code == "1A00").Subject;

        cholera.Properties.Should().ContainKey("chapter").WhoseValue.Should().Be("01");
        cholera.Properties.Should().ContainKey("is-leaf").WhoseValue.Should().Be("true");
        cholera.Properties.Should().ContainKey("foundation-uri")
            .WhoseValue.Should().StartWith("http://id.who.int/icd/entity/");

        // Codes residuales (.Y, .Z) tienen IsResidual=True en TSV pero el importer
        // no lo agrega a Properties hoy — ese campo no se usa todavía. Solo
        // verificamos que el code está presente y mapeado correctamente.
        var residualY = rows.Should().ContainSingle(x => x.Code == "1A03.Y").Subject;
        residualY.DisplayEs.Should().Contain("especificadas por otra Escherichia coli");
    }

    [Fact]
    public async Task Seed_capitulosDistintos_seCargan()
    {
        var rows = await GetAllAsync();
        // Fixture cubre capítulos 01 (infecciosas), 05 (endocrinas), 11 (cardio),
        // 12 (respiratorias), 23 (lesiones), 10 (digestivas).
        var chapters = rows.Select(r => r.Properties["chapter"]).Distinct().OrderBy(c => c).ToList();
        chapters.Should().Contain(["01", "05", "10", "11", "12", "23"]);
    }
}
