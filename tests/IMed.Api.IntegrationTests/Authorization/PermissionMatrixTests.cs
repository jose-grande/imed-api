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
    public async Task LosRolesDeFacturacion_cobranSinPoderLeerElExpediente()
    {
        // El punto de FAC-1: caja necesita identificar al paciente para cobrarle, y nada más.
        // Si algún día alguien le concede `record.read.any` a caja, que sea una decisión visible
        // en la pantalla de la matriz y no un descuido de esta línea.
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var org = Guid.NewGuid();

        var cajero = await resolver.ResolveAsync(org, ["cashier"]);
        cajero.Has(PermissionCatalog.BillingCollect).Should().BeTrue();
        cajero.Has(PermissionCatalog.RecordSearchDemographics).Should().BeTrue("hay que identificar a quién se le cobra");
        cajero.Has(PermissionCatalog.RecordReadAny).Should().BeFalse("caja no tiene por qué leer historias clínicas");
        cajero.Has(PermissionCatalog.BillingSupervise).Should().BeFalse("el cajero no autoriza sus propios descuentos");

        var facturador = await resolver.ResolveAsync(org, ["billing_clerk"]);
        facturador.Has(PermissionCatalog.BillingCharge).Should().BeTrue();
        facturador.Has(PermissionCatalog.BillingCollect).Should().BeFalse("facturar no es cobrar");

        var jefe = await resolver.ResolveAsync(org, ["billing_supervisor"]);
        jefe.Has(PermissionCatalog.BillingSupervise).Should().BeTrue();
        jefe.Has(PermissionCatalog.BillingCollect).Should().BeTrue("cubre la caja cuando hace falta");
        jefe.Has(PermissionCatalog.RecordReadAny).Should().BeFalse();
    }

    [Fact]
    public async Task LosRolesDeFarmaciaYBodega_existenSinConcederNada()
    {
        // Se declararon en el realm junto con los de facturación para tocarlo una sola vez. Que
        // existan no puede significar que abran algo: sus módulos son U2 y U3.
        using var scope = fixture.Factory.Services.CreateScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IPermissionResolver>();
        var org = Guid.NewGuid();

        foreach (var rol in new[] { "pharmacist", "pharmacy_tech", "warehouse_keeper", "inventory_manager" })
        {
            var permisos = await resolver.ResolveAsync(org, [rol]);
            permisos.Has(PermissionCatalog.BillingRead).Should().BeFalse($"'{rol}' no es de facturación");
            permisos.Has(PermissionCatalog.RecordReadAny).Should().BeFalse($"'{rol}' no lee expedientes");
            permisos.Has(PermissionCatalog.LabRead).Should().BeFalse($"'{rol}' no es del laboratorio");
        }

        // Pero sí tienen que estar listados, porque es lo que permite configurarlos.
        PermissionCatalog.Roles.Should().Contain(["pharmacist", "pharmacy_tech", "warehouse_keeper", "inventory_manager"]);
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

    [Fact]
    public async Task LaPantalla_muestraElOrigenDeCadaConcesion_yPermiteCambiarla()
    {
        var (client, orgId) = await AdminOrgAsync();

        var matriz = await (await client.GetAsync("/api/authorization/matrix"))
            .Content.ReadFromJsonAsync<MatrixProbe>();
        matriz.Should().NotBeNull();
        matriz!.Roles.Should().Contain("lab_tech");

        var labRead = matriz.Permissions.Single(p => p.Code == PermissionCatalog.LabRead);
        labRead.ByRole["lab_tech"].Should().Be("Template", "viene de la plantilla del producto");
        labRead.ByRole["auxiliar"].Should().Be("None", "nadie se lo concede");

        // Quitar en esta organización algo que la plantilla concede.
        (await client.PutAsJsonAsync("/api/authorization/matrix", new
        {
            roleCode = "lab_tech",
            permissionCode = PermissionCatalog.LabRead,
            action = "Deny",
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tras = await (await client.GetAsync("/api/authorization/matrix"))
            .Content.ReadFromJsonAsync<MatrixProbe>();
        tras!.Permissions.Single(p => p.Code == PermissionCatalog.LabRead)
            .ByRole["lab_tech"].Should().Be("Denied");

        // Volver a la plantilla borra la variante, no congela el valor de hoy.
        (await client.PutAsJsonAsync("/api/authorization/matrix", new
        {
            roleCode = "lab_tech",
            permissionCode = PermissionCatalog.LabRead,
            action = "Inherit",
        })).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var final = await (await client.GetAsync("/api/authorization/matrix"))
            .Content.ReadFromJsonAsync<MatrixProbe>();
        final!.Permissions.Single(p => p.Code == PermissionCatalog.LabRead)
            .ByRole["lab_tech"].Should().Be("Template");
    }

    [Fact]
    public async Task NadieSePuedeQuitarLaAdministracionDeLaPolitica()
    {
        var (client, _) = await AdminOrgAsync();

        var resp = await client.PutAsJsonAsync("/api/authorization/matrix", new
        {
            roleCode = "org_admin",
            permissionCode = PermissionCatalog.OrgPolicyManage,
            action = "Deny",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "la organización quedaría sin nadie que pueda volver a cambiar la política");
    }

    [Fact]
    public async Task UnRolSinOrgPolicyManage_noPuedeVerLaMatriz()
    {
        var (_, orgId) = await AdminOrgAsync();
        var tecnicoId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(tecnicoId, "tec", "tec@imed.test");
        var tecnico = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            tecnicoId, organizationId: orgId.ToString(), realmRoles: ["lab_tech"]));

        (await tecnico.GetAsync("/api/authorization/matrix")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "quien puede cambiar la política puede darse cualquier otro permiso");
    }

    /// <summary>Organización real con un usuario org_admin sobre ella.</summary>
    private async Task<(HttpClient Client, Guid OrgId)> AdminOrgAsync()
    {
        await Task.Delay(800);
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId, "duenio", "duenio@imed.test");
        var bootstrapClient = fixture.CreateClientWithToken(
            fixture.Keycloak.IssueUserToken(userId, preferredUsername: "duenio"));
        var bootstrap = await (await bootstrapClient.PostAsJsonAsync("/api/organizations/bootstrap", new
        {
            name = "Clínica Matriz",
            nationalTaxId = "0801-1990-33333",
        })).Content.ReadFromJsonAsync<BootstrapProbe>();

        var token = fixture.Keycloak.IssueUserToken(
            userId, organizationId: bootstrap!.OrganizationId.ToString(), realmRoles: ["org_admin"]);
        return (fixture.CreateClientWithToken(token), bootstrap.OrganizationId);
    }

    private sealed record BootstrapProbe(Guid OrganizationId);

    private sealed record MatrixProbe(
        IReadOnlyList<string> Roles, IReadOnlyList<MatrixRowProbe> Permissions);

    private sealed record MatrixRowProbe(
        string Code, string Module, string Description, IReadOnlyDictionary<string, string> ByRole);
}
