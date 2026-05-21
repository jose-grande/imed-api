using FluentAssertions;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Terminology;

/// <summary>
/// Tests del importer LOINC verificando: filtro <c>STATUS=ACTIVE</c>, JOIN
/// main+linguistic-variant, fallback ES→EN con flag, designations adicionales,
/// mapping a Properties JSONB.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class LoincImporterTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<List<LoincCode>> GetAllAsync()
    {
        await Task.Delay(800);
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.LoincCatalog.AsNoTracking().ToListAsync();
    }

    [Fact]
    public async Task Seed_filtraStatusNoActive()
    {
        var rows = await GetAllAsync();
        // Fixture: 20 rows (1 DEPRECATED 6298-4, 1 DISCOURAGED 30000-1, 18 ACTIVE).
        rows.Should().HaveCount(18, "DEPRECATED y DISCOURAGED se descartan");
        rows.Should().NotContain(x => x.Code == "6298-4");
        rows.Should().NotContain(x => x.Code == "30000-1");
    }

    [Fact]
    public async Task Seed_joinMainConVariantePrimaria_traduceDisplayEs()
    {
        var rows = await GetAllAsync();
        var hemoglobina = rows.Should().ContainSingle(x => x.Code == "718-7").Subject;
        hemoglobina.DisplayEs.Should().Contain("Hemoglobina", "viene de esMX28LinguisticVariant.csv");
        hemoglobina.DisplayEn.Should().Be("Hemoglobin [Mass/volume] in Blood", "viene del main file");
        hemoglobina.Properties.Should().NotContainKey("translation-pending");
    }

    [Fact]
    public async Task Seed_codigoSinTraduccion_fallbackInglesConFlag()
    {
        var rows = await GetAllAsync();
        // 33914-3 (GFR), 1920-8 (AST), 1751-7 (Albumin), 4548-4 (HbA1c), 5778-6 (Color), 33747-0 (General appearance)
        // están en el main pero NO en esMX28.
        var gfr = rows.Should().ContainSingle(x => x.Code == "33914-3").Subject;
        gfr.DisplayEs.Should().Be(gfr.DisplayEn, "fallback display_es=display_en");
        gfr.Properties.Should().ContainKey("translation-pending").WhoseValue.Should().Be("true");

        var albumin = rows.Should().ContainSingle(x => x.Code == "1751-7").Subject;
        albumin.Properties.Should().ContainKey("translation-pending");
    }

    [Fact]
    public async Task Seed_designationsAdicionales_seCarganDesdeVariantsSecundarios()
    {
        var rows = await GetAllAsync();
        // esES12LinguisticVariant tiene traducciones para 718-7, 4544-3, 2345-7.
        var hemoglobina = rows.Should().ContainSingle(x => x.Code == "718-7").Subject;
        hemoglobina.Properties.Should().ContainKey("designations",
            "esES12 está configurado como AdditionalVariant — sus traducciones van como JSON-array en properties");
        hemoglobina.Properties["designations"].Should().Contain("es-ES");
        hemoglobina.Properties["designations"].Should().Contain("España");

        // 718-7 tiene esES → designations populated. 6690-2 NO tiene esES → sin designations.
        var leucocitos = rows.Should().ContainSingle(x => x.Code == "6690-2").Subject;
        leucocitos.Properties.Should().NotContainKey("designations");
    }

    [Fact]
    public async Task Seed_pueblaPropertiesEsperadasDelMainFile()
    {
        var rows = await GetAllAsync();
        var hemoglobina = rows.Should().ContainSingle(x => x.Code == "718-7").Subject;

        hemoglobina.Properties.Should().ContainKey("class").WhoseValue.Should().Be("HEM/BC");
        hemoglobina.Properties.Should().ContainKey("class-type").WhoseValue.Should().Be("1");
        hemoglobina.Properties.Should().ContainKey("property").WhoseValue.Should().Be("MCnc");
        hemoglobina.Properties.Should().ContainKey("scale-type").WhoseValue.Should().Be("Qn");
        hemoglobina.Properties.Should().ContainKey("system").WhoseValue.Should().Be("Bld");
        hemoglobina.Properties.Should().ContainKey("time-aspect").WhoseValue.Should().Be("Pt");
        hemoglobina.Properties.Should().ContainKey("short-name").WhoseValue.Should().Be("Hgb Bld");
        hemoglobina.Properties.Should().ContainKey("order-obs").WhoseValue.Should().Be("Both");

        // Method puede estar vacío en LOINC; cuando lo está NO se guarda.
        hemoglobina.Properties.Should().NotContainKey("method-type");

        // Panel para 24356-8 (urinalysis macro panel)
        var panel = rows.Should().ContainSingle(x => x.Code == "24356-8").Subject;
        panel.Properties.Should().ContainKey("is-panel").WhoseValue.Should().Be("Panel");
    }
}
