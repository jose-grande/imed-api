using FluentAssertions;
using IMed.Api.Application.Abstractions.Terminology;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Terminology;

/// <summary>
/// Tests del contrato del port <see cref="ITerminologyService"/>.
///
/// <para>Estos tests deben pasar contra <b>cualquier</b> implementación del port —
/// hoy <c>PostgresTerminologyService</c>, mañana <c>HapiTerminologyService</c> o
/// <c>SnowstormTerminologyService</c>. Cuando aparezcan adapters adicionales,
/// convertir las facts a theories parametrizadas con MemberData del adapter
/// bajo test.</para>
///
/// <para>Lo que <b>no</b> está acá: tests con datos reales (search devuelve
/// resultados, lookup encuentra códigos). Esos viven en specs específicos por
/// CodeSystem (ej. <c>DiagnosesSearchTests</c> para ICD-11 en N2). Acá solo
/// validamos el comportamiento independiente del seed.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class TerminologyServiceContractTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ITerminologyService Resolve(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITerminologyService>();

    [Fact]
    public async Task SearchAsync_systemSinReaderRegistrado_lanzaUnsupportedCodeSystem()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = Resolve(scope);

        var act = async () => await service.SearchAsync(new TerminologySearchRequest
        {
            System = "http://example.org/some-unknown-system",
            Term = "anything"
        });

        var ex = await act.Should().ThrowAsync<UnsupportedCodeSystemException>();
        ex.Which.System.Should().Be("http://example.org/some-unknown-system");
    }

    [Fact]
    public async Task LookupAsync_systemSinReaderRegistrado_lanzaUnsupportedCodeSystem()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = Resolve(scope);

        var act = async () => await service.LookupAsync(
            "http://example.org/some-unknown-system",
            "ANY-CODE");

        await act.Should().ThrowAsync<UnsupportedCodeSystemException>();
    }

    [Fact]
    public async Task ValidateCodeAsync_systemSinReaderRegistrado_lanzaUnsupportedCodeSystem()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var service = Resolve(scope);

        var act = async () => await service.ValidateCodeAsync(
            "http://example.org/some-unknown-system",
            "ANY-CODE");

        await act.Should().ThrowAsync<UnsupportedCodeSystemException>();
    }

    [Fact]
    public void ITerminologyService_seResuelveDesdeDI()
    {
        // Sanidad: AddTerminology() registró el port.
        using var scope = fixture.Factory.Services.CreateScope();
        var service = Resolve(scope);
        service.Should().NotBeNull();
    }
}
