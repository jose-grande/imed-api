using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Billing.Fiscal;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Billing;

/// <summary>
/// Pantallas del motor fiscal por HTTP: autorizaciones (B23) y cajas (B24).
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class BillingFiscalEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(HttpClient Client, Guid OrgId, Guid LocationId, Guid TypeId)> ArrangeAsync(
        params string[] roles)
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var orgId = Guid.NewGuid();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sede = TestSedes.Create(db, orgId, "CENTRAL", "Sede central", isDefault: true);
        sede.SetFiscalEstablishmentCode("001");
        db.OrganizationLocations.Add(sede);
        var tipo = FiscalDocumentType.Create(orgId, "factura", "Factura", "01", true, 1);
        db.FiscalDocumentTypes.Add(tipo);
        await db.SaveChangesAsync();

        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(),
            realmRoles: roles.Length == 0 ? ["billing_supervisor"] : roles);

        return (fixture.CreateClientWithToken(token), orgId, sede.Id, tipo.Id);
    }

    private static object AuthBody(Guid locationId, Guid typeId, long from = 1, long to = 500, string? limit = null) => new
    {
        locationId,
        documentTypeId = typeId,
        cai = "A1B2C3-D4E5F6-A7B8C9-D0E1F2-A3B4C5-D6",
        emissionPointCode = "002",
        rangeFrom = from,
        rangeTo = to,
        limitDate = limit ?? "2027-12-31",
    };

    [Fact]
    public async Task RegistrarUnCai_loDejaListoParaEmitir()
    {
        var (client, _, locationId, typeId) = await ArrangeAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/billing/fiscal/authorizations", AuthBody(locationId, typeId));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<FiscalAuthorizationDto>();
        dto!.Status.Should().Be("Active");
        dto.NextSequence.Should().Be(1, "arranca en el primero del rango");
        dto.Remaining.Should().Be(500);
        dto.Alert.Should().BeNull("con 500 documentos y un año por delante no hay nada que avisar");
    }

    [Fact]
    public async Task UnCaiConFechaYaPasada_seRechazaAlRegistrarlo()
    {
        // Descubrir que la autorización nació vencida al intentar cobrar es lo peor que puede
        // pasar: el paciente está enfrente. Se rechaza en la captura.
        var (client, _, locationId, typeId) = await ArrangeAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/billing/fiscal/authorizations",
            AuthBody(locationId, typeId, limit: "2020-01-01"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task QuedanPocosDocumentos_laPantallaLoAvisaAntesDeQuedarseSinNinguno()
    {
        // El valor operativo de B23: avisar mientras todavía se puede pedir un rango nuevo.
        var (client, _, locationId, typeId) = await ArrangeAsync();

        var resp = await client.PostAsJsonAsync(
            "/api/billing/fiscal/authorizations", AuthBody(locationId, typeId, from: 1, to: 10));
        var dto = await resp.Content.ReadFromJsonAsync<FiscalAuthorizationDto>();

        dto!.Alert.Should().NotBeNull();
        dto.Alert.Should().Contain("10", "el aviso dice cuántos quedan, no solo que quedan pocos");
    }

    [Fact]
    public async Task DarDeBajaUnaAutorizacion_laDejaSinEmitir()
    {
        var (client, _, locationId, typeId) = await ArrangeAsync();
        var creada = await (await client.PostAsJsonAsync(
            "/api/billing/fiscal/authorizations", AuthBody(locationId, typeId)))
            .Content.ReadFromJsonAsync<FiscalAuthorizationDto>();

        var resp = await client.PostAsync(
            $"/api/billing/fiscal/authorizations/{creada!.Id}/revoke", null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var baja = await resp.Content.ReadFromJsonAsync<FiscalAuthorizationDto>();
        baja!.Status.Should().Be("Revoked");
        baja.Alert.Should().Contain("no emite");
    }

    [Fact]
    public async Task ElCajero_veLasAutorizacionesPeroNoLasDaDeAlta()
    {
        // Saber con qué CAI va a emitir es parte de su trabajo; comprometer a la organización
        // ante la autoridad fiscal, no.
        var (client, _, locationId, typeId) = await ArrangeAsync("cashier");

        (await client.GetAsync("/api/billing/fiscal/authorizations"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await client.PostAsJsonAsync("/api/billing/fiscal/authorizations", AuthBody(locationId, typeId)))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ElPersonalClinico_noEntraAlMotorFiscal()
    {
        var (client, _, _, _) = await ArrangeAsync("practitioner");

        (await client.GetAsync("/api/billing/fiscal/authorizations"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UnaCajaNueva_quedaOperativaSinReiniciarNada()
    {
        // Una clínica abre una caja extra el día que hay fila. Si exigiera un trámite previo, la
        // caja extra se abre igual pero por fuera del sistema.
        var (client, _, locationId, _) = await ArrangeAsync();

        var resp = await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId,
            code = "CAJA3",
            name = "Caja 3",
            emissionPointCode = "002",
            profile = "Services",
            isTemporary = true,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var caja = await resp.Content.ReadFromJsonAsync<CashPointDto>();
        caja!.Active.Should().BeTrue();
        caja.IsTemporary.Should().BeTrue();

        var listado = await (await client.GetAsync("/api/billing/fiscal/cash-points"))
            .Content.ReadFromJsonAsync<CashPointDto[]>();
        listado!.Should().Contain(c => c.Code == "CAJA3");
    }

    [Fact]
    public async Task LaPantallaDice_conQueOtrasCajasCompartePuntoDeEmision()
    {
        // Compartir punto de emisión significa compartir la secuencia. Quien da de alta la caja
        // tiene que saberlo antes de guardar, no cuando el correlativo salte.
        var (client, _, locationId, _) = await ArrangeAsync();

        foreach (var (code, name) in new[] { ("CAJA1", "Caja 1"), ("CAJA2", "Caja 2") })
        {
            await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
            {
                locationId, code, name, emissionPointCode = "002",
            });
        }

        var tercera = await (await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId, code = "CAJA3", name = "Caja 3", emissionPointCode = "002",
        })).Content.ReadFromJsonAsync<CashPointDto>();

        tercera!.EmissionPointSharedWith.Should().BeEquivalentTo(["Caja 1", "Caja 2"]);
    }

    [Fact]
    public async Task UnaCajaConPuntoPropio_noCompartaConNadie()
    {
        // La otra estrategia de QX1: un punto de emisión por terminal. El código soporta las dos.
        var (client, _, locationId, _) = await ArrangeAsync();

        await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId, code = "CAJA1", name = "Caja 1", emissionPointCode = "001",
        });

        var segunda = await (await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId, code = "CAJA2", name = "Caja 2", emissionPointCode = "002",
        })).Content.ReadFromJsonAsync<CashPointDto>();

        segunda!.EmissionPointSharedWith.Should().BeEmpty();
    }

    [Fact]
    public async Task DosCajasConElMismoCodigo_seRechaza()
    {
        var (client, _, locationId, _) = await ArrangeAsync();
        var body = new { locationId, code = "CAJA1", name = "Caja 1", emissionPointCode = "001" };

        (await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", body))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", body))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CerrarUnaCaja_noLaBorra()
    {
        // Los turnos, pagos y documentos que la referencian son historia y tienen que seguir
        // resolviendo de qué caja salieron.
        var (client, _, locationId, _) = await ArrangeAsync();
        var caja = await (await client.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId, code = "CAJA1", name = "Caja 1", emissionPointCode = "001",
        })).Content.ReadFromJsonAsync<CashPointDto>();

        var resp = await client.PatchAsJsonAsync(
            $"/api/billing/fiscal/cash-points/{caja!.Id}", new { active = false });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var listado = await (await client.GetAsync("/api/billing/fiscal/cash-points"))
            .Content.ReadFromJsonAsync<CashPointDto[]>();
        listado!.Should().ContainSingle(c => c.Id == caja.Id && !c.Active);
    }

    [Fact]
    public async Task LasCajasDeOtraOrganizacion_noSeVen()
    {
        var (clientA, _, locationA, _) = await ArrangeAsync();
        await clientA.PostAsJsonAsync("/api/billing/fiscal/cash-points", new
        {
            locationId = locationA, code = "CAJA1", name = "Caja de la clínica A", emissionPointCode = "001",
        });

        var (clientB, _, _, _) = await ArrangeAsync();
        var listado = await (await clientB.GetAsync("/api/billing/fiscal/cash-points"))
            .Content.ReadFromJsonAsync<CashPointDto[]>();

        listado!.Should().BeEmpty();
    }
}
