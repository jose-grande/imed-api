using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using IMed.Api.IntegrationTests.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IMed.Api.IntegrationTests.Organizations;

/// <summary>
/// Logo de la organización por HTTP: el primer endpoint multipart de la API.
/// </summary>
/// <remarks>
/// <para>Observación de QA (ronda 1, staging): «necesitamos agregarle el formato con logo de la
/// clínica». Acá se ejercita el camino completo —multipart, permiso, validación por contenido,
/// ida y vuelta por PostgreSQL— porque es donde se junta todo lo que las pruebas unitarias
/// miran por separado.</para>
///
/// <para>Hay además una traducción que no se puede dar por buena: la consulta de
/// <c>/info</c> proyecta <c>LogoBytes.Length</c> para que el tamaño lo calcule el servidor y la
/// imagen no viaje. Si Npgsql no supiera traducirlo, fallaría acá y no al compilar.</para>
/// </remarks>
[Collection(ImedApiCollection.Name)]
public sealed class OrganizationLogoEndpointsTests(ImedApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private const string Url = "/api/organizations/me/logo";

    private sealed record LogoResponse(
        bool HasLogo,
        string? ContentType,
        string? FileName,
        int? SizeBytes,
        DateTimeOffset? UpdatedAt,
        int MaxSizeBytes,
        string[] AllowedContentTypes);

    private HttpClient ClientAs(params string[] roles)
    {
        var userId = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(userId);
        var token = fixture.Keycloak.IssueUserToken(
            userId,
            organizationId: Guid.NewGuid().ToString(),
            realmRoles: roles.Length == 0 ? ["org_admin"] : roles);
        return fixture.CreateClientWithToken(token);
    }

    private static MultipartFormDataContent Archivo(
        byte[] bytes, string nombre, string contentType)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new MultipartFormDataContent { { content, "file", nombre } };
    }

    [Fact]
    public async Task Sin_logo_el_info_responde_que_no_hay_y_trae_los_limites()
    {
        var client = ClientAs();

        var response = await client.GetAsync($"{Url}/info");

        // 200 y no 404: la pantalla pregunta «¿hay logo?», y esa pregunta tiene respuesta.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<LogoResponse>();
        body!.HasLogo.Should().BeFalse();

        // Los límites viajan al cliente para que la pantalla no los repita por su cuenta.
        body.MaxSizeBytes.Should().Be(1024 * 1024);
        body.AllowedContentTypes.Should().BeEquivalentTo(["image/png", "image/jpeg"]);
    }

    [Fact]
    public async Task Sin_logo_la_imagen_responde_404()
    {
        var client = ClientAs();

        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sube_un_png_y_lo_devuelve_igual()
    {
        var client = ClientAs();
        var png = Png(300, 120);

        var put = await client.PutAsync(Url, Archivo(png, "logo.png", "image/png"));
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetAsync(Url);
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        get.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await get.Content.ReadAsByteArrayAsync()).Should().Equal(
            png, "un bytea no puede alterar un solo byte");
    }

    [Fact]
    public async Task El_info_informa_el_tamano_calculado_en_la_base()
    {
        // Si `LogoBytes.Length` no se tradujera a `length(bytea)`, esto fallaría acá.
        var client = ClientAs();
        var png = Png(300, 120);

        await client.PutAsync(Url, Archivo(png, "marca.png", "image/png"));

        var body = await (await client.GetAsync($"{Url}/info"))
            .Content.ReadFromJsonAsync<LogoResponse>();

        body!.HasLogo.Should().BeTrue();
        body.SizeBytes.Should().Be(png.Length);
        body.FileName.Should().Be("marca.png");
        body.UpdatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task El_tipo_guardado_sale_de_los_bytes_y_no_del_multipart()
    {
        // Un JPEG subido declarando `image/png`. El servidor mira el contenido.
        var client = ClientAs();

        await client.PutAsync(Url, Archivo(Jpeg(300, 120), "logo.png", "image/png"));

        var get = await client.GetAsync(Url);
        get.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
    }

    [Fact]
    public async Task Rechaza_un_archivo_que_no_es_imagen()
    {
        var client = ClientAs();
        var basura = System.Text.Encoding.UTF8.GetBytes(new string('x', 4096));

        var put = await client.PutAsync(Url, Archivo(basura, "logo.png", "image/png"));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // El motivo tiene que llegar: con un mensaje genérico el usuario no sabe si el problema
        // es el formato, el tamaño o las dimensiones.
        (await put.Content.ReadAsStringAsync()).Should().Contain("no es una imagen");
    }

    [Fact]
    public async Task Rechaza_un_gif_aunque_sea_una_imagen_de_verdad()
    {
        var client = ClientAs();

        var put = await client.PutAsync(Url, Archivo(Gif(200, 80), "logo.gif", "image/gif"));

        put.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await put.Content.ReadAsStringAsync()).Should().Contain("PNG o JPEG");
    }

    [Fact]
    public async Task Reemplazar_el_logo_no_deja_el_anterior()
    {
        var client = ClientAs();
        await client.PutAsync(Url, Archivo(Png(200, 80), "uno.png", "image/png"));

        var segundo = Jpeg(400, 100);
        await client.PutAsync(Url, Archivo(segundo, "dos.jpg", "image/jpeg"));

        var body = await (await client.GetAsync($"{Url}/info"))
            .Content.ReadFromJsonAsync<LogoResponse>();
        body!.FileName.Should().Be("dos.jpg");
        body.SizeBytes.Should().Be(segundo.Length);
    }

    [Fact]
    public async Task Quita_el_logo()
    {
        var client = ClientAs();
        await client.PutAsync(Url, Archivo(Png(200, 80), "logo.png", "image/png"));

        var delete = await client.DeleteAsync(Url);

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await (await client.GetAsync($"{Url}/info"))
            .Content.ReadFromJsonAsync<LogoResponse>();
        body!.HasLogo.Should().BeFalse();
    }

    [Fact]
    public async Task Quitar_un_logo_que_no_existe_responde_404()
    {
        var client = ClientAs();

        (await client.DeleteAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sin_permiso_de_administracion_no_se_puede_cargar_ni_quitar()
    {
        // Un recepcionista no define la identidad visual de la clínica.
        var client = ClientAs("lab_receptionist");

        (await client.PutAsync(Url, Archivo(Png(200, 80), "logo.png", "image/png")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.DeleteAsync(Url)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Pero_cualquiera_de_la_organizacion_puede_verlo()
    {
        // La lectura solo pide estar autenticado: el logo aparece en documentos que esos mismos
        // usuarios imprimen, así que esconderlo no protegería nada.
        // Los dos clientes comparten tenant, que es lo que el test necesita; `ClientAs` genera
        // un organization_id nuevo en cada llamada, así que acá se arman a mano.
        var orgId = Guid.NewGuid();

        var adminUser = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(adminUser);
        var admin = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            adminUser, organizationId: orgId.ToString(), realmRoles: ["org_admin"]));

        var otroUser = Guid.NewGuid();
        fixture.Keycloak.RegisterUser(otroUser);
        var otro = fixture.CreateClientWithToken(fixture.Keycloak.IssueUserToken(
            otroUser, organizationId: orgId.ToString(), realmRoles: ["lab_receptionist"]));

        await admin.PutAsync(Url, Archivo(Png(200, 80), "logo.png", "image/png"));

        (await otro.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Una_organizacion_no_ve_el_logo_de_otra()
    {
        var unaOrg = ClientAs("org_admin");
        await unaOrg.PutAsync(Url, Archivo(Png(200, 80), "ajeno.png", "image/png"));

        // Otro tenant: `ClientAs` genera un organization_id nuevo cada vez.
        var otraOrg = ClientAs("org_admin");

        (await otraOrg.GetAsync(Url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sin_token_no_se_llega()
    {
        var anonimo = fixture.Factory.CreateClient();

        (await anonimo.GetAsync($"{Url}/info"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Imágenes de prueba ──────────────────────────────────────────────────

    private static byte[] Png(int w, int h) => Imagen(w, h, (img, ms) => img.SaveAsPng(ms));
    private static byte[] Jpeg(int w, int h) => Imagen(w, h, (img, ms) => img.SaveAsJpeg(ms));
    private static byte[] Gif(int w, int h) => Imagen(w, h, (img, ms) => img.SaveAsGif(ms));

    private static byte[] Imagen(int w, int h, Action<Image<Rgba32>, MemoryStream> guardar)
    {
        using var img = new Image<Rgba32>(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                img[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }
        }
        using var ms = new MemoryStream();
        guardar(img, ms);
        return ms.ToArray();
    }
}
