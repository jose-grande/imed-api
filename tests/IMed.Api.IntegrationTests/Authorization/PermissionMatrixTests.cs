using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Authorization;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IMed.Api.IntegrationTests.Authorization;

/// <summary>
/// Matriz de permisos (ADR-007, SEC-2): la política es dato, no roles quemados en el arranque.
///
/// <para>Lo que importa probar acá no es que un rol pueda entrar —eso lo cubre el resto de la
/// suite, que sigue verde tras la migración y es la prueba de paridad— sino las tres propiedades
/// que la matriz agrega: que se pueda sobreescribir por organización, que revocar surta efecto
/// sin esperar el TTL de la caché, y que un tenant no vea la variante de otro.</para>
/// </summary>
[Collection(ImedApiCollection.Name)]
public sealed class PermissionMatrixTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LaPlantillaGlobal_reproduceLosRolesQueTeniaCadaPoliticaQuemada()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var org = Guid.NewGuid();

        // Paridad exacta con las cuatro políticas previas de Program.cs:99-109.
        (await resolver.ResolveAsync(org, ["practitioner"])).Has(PermissionCatalog.LabRead).Should().BeTrue();
        (await resolver.ResolveAsync(org, ["practitioner"])).Has(PermissionCatalog.LabOperate).Should().BeFalse();
        (await resolver.ResolveAsync(org, ["lab_tech"])).Has(PermissionCatalog.LabOperate).Should().BeTrue();
        (await resolver.ResolveAsync(org, ["lab_tech"])).Has(PermissionCatalog.LabValidate).Should().BeFalse();
        (await resolver.ResolveAsync(org, ["bioanalyst"])).Has(PermissionCatalog.LabValidate).Should().BeTrue();
        (await resolver.ResolveAsync(org, ["bioanalyst"])).Has(PermissionCatalog.LabManage).Should().BeFalse();
        (await resolver.ResolveAsync(org, ["lab_supervisor"])).Has(PermissionCatalog.LabManage).Should().BeTrue();

        // Y lo que la matriz agrega: recepción encuentra pacientes sin leer su historia.
        var auxiliar = await resolver.ResolveAsync(org, ["auxiliar"]);
        auxiliar.Has(PermissionCatalog.RecordSearchDemographics).Should().BeTrue();
        auxiliar.Has(PermissionCatalog.RecordReadAny).Should().BeFalse();
        auxiliar.Has(PermissionCatalog.LabRead).Should().BeFalse();
    }

    [Fact]
    public async Task RevocarUnPermiso_surteEfectoSinEsperarElTtl()
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "tecnico", "tecnico@imed.test");
        var bootstrapClient = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "tecnico"));
        var bootstrap = await (await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica Revocación",
            nationalTaxId = "0801-1990-22222",
        })).Content.ReadFromJsonAsync<BootstrapProbe>();
        var orgId = bootstrap!.OrganizationId;
        await fixture.ProvisionLabCatalogAsync(orgId);

        var client = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            userId, organizationId: orgId.ToString(), realmRoles: ["lab_tech"]));

        // Con la plantilla global, un lab_tech ve el catálogo de laboratorio.
        (await client.GetAsync("/api/lab-catalog/tests")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        // La organización le quita lab.read a su rol lab_tech. La caché ya tiene la respuesta
        // anterior: si la invalidación no funcionara, el técnico seguiría entrando.
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
            db.RolePermissions.Add(RolePermission.Create(
                "lab_tech", PermissionCatalog.LabRead, PermissionScope.Organization,
                organizationId: orgId, denied: true));
            await db.SaveChangesAsync();
            await resolver.InvalidateAsync(orgId, null);
        }

        (await client.GetAsync("/api/lab-catalog/tests")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "revocar tiene que valer en el request siguiente, no cuando venza el TTL");
    }

    [Fact]
    public async Task LaVarianteDeUnaOrganizacion_noAfectaALasDemas()
    {
        var conRestriccion = Guid.NewGuid();
        var sinRestriccion = Guid.NewGuid();

        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        db.RolePermissions.Add(RolePermission.Create(
            "pathologist", PermissionCatalog.LabValidate, PermissionScope.Organization,
            organizationId: conRestriccion, denied: true));
        await db.SaveChangesAsync();
        await resolver.InvalidateAsync(conRestriccion, null);

        (await resolver.ResolveAsync(conRestriccion, ["pathologist"]))
            .Has(PermissionCatalog.LabValidate).Should().BeFalse();
        (await resolver.ResolveAsync(sinRestriccion, ["pathologist"]))
            .Has(PermissionCatalog.LabValidate).Should().BeTrue(
                "una decisión de un tenant no puede filtrarse a otro");
    }

    [Fact]
    public async Task VariosRoles_seSuman_yGanaElAlcanceMasAmplio()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var org = Guid.NewGuid();

        var ambos = await resolver.ResolveAsync(org, ["lab_tech", "bioanalyst"]);

        ambos.Has(PermissionCatalog.LabOperate).Should().BeTrue("lo aporta lab_tech");
        ambos.Has(PermissionCatalog.LabValidate).Should().BeTrue("lo aporta bioanalyst");
        ambos.ScopeOf(PermissionCatalog.LabRead).Should().Be(PermissionScope.Organization);
    }

    [Fact]
    public async Task SinRoles_noHayPermisos()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();

        var vacio = await resolver.ResolveAsync(Guid.NewGuid(), []);

        vacio.Codes.Should().BeEmpty("default deny: sin rol no se concede nada");
    }

    [Fact]
    public async Task ElCatalogoSembrado_cubreTodoLoQueDeclaraElCodigo()
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var sembrados = await db.Permissions.AsNoTracking().Select(p => p.Code).ToListAsync();

        sembrados.Should().BeEquivalentTo(
            PermissionCatalog.All.Select(p => p.Code),
            "el seeder reconcilia: un permiso agregado al catálogo tiene que llegar a las " +
            "instalaciones existentes, no solo a las nuevas");
    }

    private sealed record BootstrapProbe(Guid OrganizationId);
}
