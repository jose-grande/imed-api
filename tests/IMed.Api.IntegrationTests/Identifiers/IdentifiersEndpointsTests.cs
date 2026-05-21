using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Identifiers;
using IMed.Api.IntegrationTests.Infrastructure;
using Xunit;

namespace IMed.Api.IntegrationTests.Identifiers;

/// <summary>
/// Tests del registry de identifier types (Épica H1). La config viva en
/// <c>appsettings.json</c> con los 4 tipos HN (IdentidadNacional, Pasaporte,
/// RTN, CarnetResidente). Los tests confían en esa config sin re-declararla.
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class IdentifiersEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private HttpClient AuthedClient()
    {
        var token = fixture.Keycloak.IssueUserToken(Guid.NewGuid());
        return fixture.CreateClientWithToken(token);
    }

    // -------- GET /api/identifiers/types --------

    [Fact]
    public async Task GET_types_sin_auth_devuelve_401()
    {
        var client = fixture.Factory.CreateClient();
        var resp = await client.GetAsync("/api/identifiers/types?country=HN");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GET_types_sin_country_devuelve_400()
    {
        var client = AuthedClient();
        var resp = await client.GetAsync("/api/identifiers/types");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GET_types_HN_devuelve_los_4_tipos_esperados()
    {
        var client = AuthedClient();
        var resp = await client.GetAsync("/api/identifiers/types?country=HN");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await resp.Content.ReadFromJsonAsync<IdentifierTypeDto[]>();
        types.Should().NotBeNull();
        types!.Select(t => t.Code).Should().BeEquivalentTo(
            new[] { "IdentidadNacional", "Pasaporte", "RTN", "CarnetResidente" });

        var dni = types.Single(t => t.Code == "IdentidadNacional");
        dni.Label.Should().Be("Identidad Nacional");
        dni.FhirSystemUri.Should().Be("https://imed.app/identifiers/hn/dni");
        dni.NormalizationHint.Should().Be("NNNN-NNNN-NNNNN");
        dni.Hl7V2Code.Should().Be("NI");

        types.Single(t => t.Code == "Pasaporte").Hl7V2Code.Should().Be("PPN");
        types.Single(t => t.Code == "RTN").Hl7V2Code.Should().Be("TAX");
        types.Single(t => t.Code == "CarnetResidente").Hl7V2Code.Should().Be("PRC");
    }

    [Fact]
    public async Task GET_types_pais_sin_config_devuelve_array_vacio()
    {
        var client = AuthedClient();
        var resp = await client.GetAsync("/api/identifiers/types?country=GT");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var types = await resp.Content.ReadFromJsonAsync<IdentifierTypeDto[]>();
        types.Should().NotBeNull().And.BeEmpty();
    }

    // -------- POST /api/identifiers/validate --------

    [Fact]
    public async Task POST_validate_dni_HN_sin_guiones_normaliza_con_guiones()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "IdentidadNacional",
            value = "0801198512345"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result.Should().NotBeNull();
        result!.Valid.Should().BeTrue();
        result.Normalized.Should().Be("0801-1985-12345");
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public async Task POST_validate_dni_HN_con_guiones_pasa_y_devuelve_canonical()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "IdentidadNacional",
            value = "0801-1985-12345"
        });
        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result!.Valid.Should().BeTrue();
        result.Normalized.Should().Be("0801-1985-12345");
    }

    [Fact]
    public async Task POST_validate_dni_HN_corto_devuelve_invalid()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "IdentidadNacional",
            value = "0801-1985-1234"
        });
        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result!.Valid.Should().BeFalse();
        result.Normalized.Should().BeNull();
        result.Errors.Should().NotBeEmpty();
        result.Errors[0].Should().Contain("Identidad Nacional");
    }

    [Fact]
    public async Task POST_validate_rtn_HN_normaliza_con_guiones_de_14_digitos()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "RTN",
            value = "08011985123456"
        });
        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result!.Valid.Should().BeTrue();
        result.Normalized.Should().Be("0801-1985-123456");
    }

    [Fact]
    public async Task POST_validate_pasaporte_minusculas_normaliza_a_uppercase()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "Pasaporte",
            value = "abc1234567"
        });
        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result!.Valid.Should().BeTrue();
        result.Normalized.Should().Be("ABC1234567");
    }

    [Fact]
    public async Task POST_validate_tipo_desconocido_devuelve_invalid_con_mensaje()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = "Curp",
            value = "ABCD850101HDFRRR01"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await resp.Content.ReadFromJsonAsync<IdentifierValidationResult>();
        result!.Valid.Should().BeFalse();
        result.Errors[0].Should().Contain("no soportado");
    }

    [Fact]
    public async Task POST_validate_request_malformado_devuelve_400()
    {
        var client = AuthedClient();
        var resp = await client.PostAsJsonAsync("/api/identifiers/validate", new
        {
            country = "HN",
            type = (string?)null,
            value = "0801-1985-12345"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
