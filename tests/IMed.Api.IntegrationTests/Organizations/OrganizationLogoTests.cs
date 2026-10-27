using System.Text;
using FluentAssertions;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Organizations;
using IMed.Api.Domain.Organizations;
using IMed.Api.Infrastructure.Imaging;
using IMed.Api.Infrastructure.Pdf;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace IMed.Api.IntegrationTests.Organizations;

/// <summary>
/// Logo de la organización en los documentos impresos.
///
/// <para>Observación de QA (ronda 1, staging): «necesitamos agregarle el formato con logo de la
/// clínica». Van cuatro documentos —informe de laboratorio, orden de exámenes, receta y
/// factura—; las etiquetas de muestra quedaron excluidas por decisión del usuario.</para>
/// </summary>
public sealed class OrganizationLogoTests
{
    private static readonly Guid OrgId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // ── Identificación de la imagen ─────────────────────────────────────────

    public sealed class Inspector
    {
        private readonly ImageSharpImageInspector _inspector = new();

        [Fact]
        public void Reconoce_un_png_y_devuelve_sus_dimensiones()
        {
            var info = _inspector.Inspect(Png(120, 48));

            info.Should().NotBeNull();
            info!.ContentType.Should().Be("image/png");
            info.Width.Should().Be(120);
            info.Height.Should().Be(48);
        }

        [Fact]
        public void Reconoce_un_jpeg()
        {
            var info = _inspector.Inspect(Jpeg(200, 80));

            info.Should().NotBeNull();
            info!.ContentType.Should().Be("image/jpeg");
        }

        [Fact]
        public void Rechaza_bytes_que_no_son_una_imagen()
        {
            // Esta es la razón de ser del inspector: un archivo cualquiera renombrado a .png
            // llega declarando `image/png`, y lo único que lo delata es el contenido.
            var texto = Encoding.UTF8.GetBytes("Esto no es una imagen, es un archivo de texto.");

            _inspector.Inspect(texto).Should().BeNull();
        }

        [Fact]
        public void Rechaza_un_png_truncado()
        {
            // Arranca con la firma correcta y se corta. Si solo se mirara la cabecera mágica,
            // esto pasaría la validación y reventaría recién al emitir el primer documento.
            var completo = Png(64, 64);
            var truncado = completo[..Math.Min(24, completo.Length)];

            _inspector.Inspect(truncado).Should().BeNull();
        }

        [Fact]
        public void Rechaza_bytes_vacios()
        {
            _inspector.Inspect([]).Should().BeNull();
            _inspector.Inspect(null!).Should().BeNull();
        }
    }

    // ── Validación al guardar ───────────────────────────────────────────────

    public sealed class Guardado
    {
        private readonly FakeBrandingRepository _repo = new();
        private readonly SetOrganizationLogoHandler _handler;

        public Guardado()
        {
            _handler = new SetOrganizationLogoHandler(
                _repo,
                new ImageSharpImageInspector(),
                new RelojFijo(new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero)));
        }

        [Fact]
        public async Task Guarda_un_png_valido()
        {
            var result = await _handler.HandleAsync(Comando(Png(240, 90), "image/png", "logo.png"));

            result.Success.Should().BeTrue(result.Error);
            _repo.Saved.Should().NotBeNull();
            _repo.Saved!.ContentType.Should().Be("image/png");
            _repo.Saved.FileName.Should().Be("logo.png");
        }

        [Fact]
        public async Task El_tipo_guardado_sale_de_los_bytes_y_no_de_lo_que_declara_el_cliente()
        {
            // Un JPEG subido con extensión y Content-Type de PNG. Si se guardara el tipo
            // declarado, el PDF llevaría `image/png` sobre bytes JPEG.
            var result = await _handler.HandleAsync(
                Comando(Jpeg(240, 90), declarado: "image/png", nombre: "logo.png"));

            result.Success.Should().BeTrue(result.Error);
            _repo.Saved!.ContentType.Should().Be("image/jpeg");
        }

        [Fact]
        public async Task Rechaza_un_archivo_que_no_es_imagen_aunque_se_declare_png()
        {
            var texto = Encoding.UTF8.GetBytes(new string('x', 2048));

            var result = await _handler.HandleAsync(Comando(texto, "image/png", "logo.png"));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("no es una imagen");
            _repo.Saved.Should().BeNull("nada debe llegar a la base");
        }

        [Fact]
        public async Task Rechaza_un_formato_de_imagen_no_admitido()
        {
            // Un GIF es una imagen de verdad: la rechaza la lista de formatos, no el inspector.
            var result = await _handler.HandleAsync(Comando(Gif(120, 60), "image/gif", "logo.gif"));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("PNG o JPEG");
            _repo.Saved.Should().BeNull();
        }

        [Fact]
        public async Task Rechaza_un_archivo_que_supera_el_maximo()
        {
            var grande = new byte[OrganizationBranding.MaxLogoBytes + 1];

            var result = await _handler.HandleAsync(Comando(grande, "image/png", "grande.png"));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("máximo");
            _repo.Saved.Should().BeNull();
        }

        [Fact]
        public async Task Rechaza_una_imagen_demasiado_chica()
        {
            // Casi siempre es un favicon subido por error; impresa es una mancha.
            var result = await _handler.HandleAsync(Comando(Png(16, 16), "image/png", "icono.png"));

            result.Success.Should().BeFalse();
            result.Error.Should().Contain("mínimo");
            _repo.Saved.Should().BeNull();
        }

        [Fact]
        public async Task Rechaza_un_archivo_vacio()
        {
            var result = await _handler.HandleAsync(Comando([], "image/png", "vacio.png"));

            result.Success.Should().BeFalse();
            _repo.Saved.Should().BeNull();
        }

        private static SetOrganizationLogoCommand Comando(
            byte[] bytes, string declarado, string nombre) =>
            new(OrgId, UpdatedBy: Guid.NewGuid(), bytes, declarado, nombre);
    }

    // ── Invariantes del dominio ─────────────────────────────────────────────

    public sealed class Dominio
    {
        [Fact]
        public void Una_organizacion_nueva_no_tiene_logo()
        {
            OrganizationBranding.Create(OrgId).HasLogo.Should().BeFalse();
        }

        [Fact]
        public void Quitar_el_logo_lo_deja_sin_logo_pero_conserva_el_rastro()
        {
            var quien = Guid.NewGuid();
            var branding = OrganizationBranding.Create(OrgId);
            branding.SetLogo(Png(100, 50), "image/png", "logo.png", Guid.NewGuid());

            branding.RemoveLogo(quien);

            branding.HasLogo.Should().BeFalse();
            branding.LogoBytes.Should().BeNull();
            branding.LogoContentType.Should().BeNull();
            branding.UpdatedBy.Should().Be(quien, "queda quién tocó la identidad visual");
        }

        [Fact]
        public void El_dominio_tambien_rechaza_un_tipo_no_admitido()
        {
            // Defensa en profundidad: el handler ya valida, pero la entidad no depende de que
            // alguien la haya llamado bien.
            var branding = OrganizationBranding.Create(OrgId);
            var accion = () => branding.SetLogo([1, 2, 3], "image/svg+xml", null, null);

            accion.Should().Throw<ArgumentException>().WithMessage("*no aceptado*");
        }
    }

    // ── El logo en el papel ─────────────────────────────────────────────────

    public sealed class Renderizado
    {
        private readonly LabReportPdfRenderer _informe = new();
        private readonly InvoicePdfRenderer _factura = new();

        [Fact]
        public void El_informe_con_logo_pesa_mas_que_sin_logo()
        {
            var sin = _informe.Render(Informe(logo: null));
            var con = _informe.Render(Informe(Logo(Png(400, 150))));

            Texto(sin).Should().StartWith("%PDF");
            Texto(con).Should().StartWith("%PDF");
            con.Length.Should().BeGreaterThan(sin.Length, "el logo tiene que estar incrustado");
        }

        [Fact]
        public void Un_logo_ilegible_no_impide_emitir_el_informe()
        {
            // El caso que importa: unos bytes corruptos en la base no pueden dejar al
            // laboratorio sin poder entregar resultados.
            var corrupto = new PdfLogo(
                Encoding.UTF8.GetBytes("esto no es una imagen"), "image/png", "corrupto");

            var pdf = _informe.Render(Informe(corrupto));

            Texto(pdf).Should().StartWith("%PDF");
            Paginas(pdf).Should().Be(Paginas(_informe.Render(Informe(logo: null))));
        }

        [Theory]
        [InlineData(2000, 100)]  // muy apaisado
        [InlineData(100, 2000)]  // muy vertical
        [InlineData(500, 500)]   // cuadrado
        public void Ninguna_proporcion_de_logo_desborda_el_encabezado(int ancho, int alto)
        {
            // Sin tope de alto, un logo de 100×2000 escalado a 3.2 cm de ancho mediría 64 cm:
            // empujaría los resultados a la página siguiente.
            var sinLogo = Paginas(_informe.Render(Informe(logo: null)));

            var pdf = _informe.Render(Informe(Logo(Png(ancho, alto))));

            Texto(pdf).Should().StartWith("%PDF");
            Paginas(pdf).Should().Be(sinLogo, "el logo se encaja en una caja, no se escala solo por ancho");
        }

        [Fact]
        public void La_factura_lleva_logo_en_carta_y_en_ticket()
        {
            var logo = Logo(Png(400, 150));

            foreach (var formato in new[] { InvoicePrintFormat.Letter, InvoicePrintFormat.Thermal80 })
            {
                var sin = _factura.Render(Factura(logo: null), formato);
                var con = _factura.Render(Factura(logo), formato);

                Texto(con).Should().StartWith("%PDF");
                con.Length.Should().BeGreaterThan(sin.Length, $"formato {formato}");
            }
        }

        [Fact]
        public void Dos_organizaciones_distintas_no_comparten_la_imagen_cacheada()
        {
            // MigraDoc cachea las imágenes por nombre y la caché es por proceso. Con una clave
            // fija, el segundo laboratorio en emitir recibiría el logo del primero.
            var a = new OrganizationLogoContent(
                Png(300, 100), "image/png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var b = new OrganizationLogoContent(
                Png(300, 100), "image/png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

            var claveA = a.ToPdfLogo(Guid.NewGuid()).CacheKey;
            var claveB = b.ToPdfLogo(Guid.NewGuid()).CacheKey;

            claveA.Should().NotBe(claveB);
        }

        [Fact]
        public void Reemplazar_el_logo_cambia_la_clave_de_cache()
        {
            // Si no cambiara, los documentos seguirían saliendo con el logo viejo hasta
            // reiniciar el servicio.
            var org = Guid.NewGuid();
            var viejo = new OrganizationLogoContent(
                Png(300, 100), "image/png", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var nuevo = new OrganizationLogoContent(
                Png(300, 100), "image/png", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

            viejo.ToPdfLogo(org).CacheKey.Should().NotBe(nuevo.ToPdfLogo(org).CacheKey);
        }

        private static PdfLogo Logo(byte[] bytes) =>
            new(bytes, "image/png", $"test-{Guid.NewGuid():N}");

        private static LabReportPdfModel Informe(PdfLogo? logo) => new(
            Organization: new PdfOrgInfo("Laboratorio Clínico de Prueba", TaxId: null, Logo: logo),
            Patient: new PdfPatientInfo("Juan Pérez", "1990-05-12", "male", "Identidad", "0801199012345"),
            Groups:
            [
                new PdfReportGroup("Hemograma", "58410-2",
                [
                    new PdfReportLine("Hemoglobina", "718-7", "14.2", "g/dL", "13.0 – 17.0",
                        null, null, false, false, false),
                ]),
            ],
            IsFinal: true,
            Requisition: "REQ-001",
            AccessionNumber: "ACC-001",
            PrintedAt: new DateTimeOffset(2026, 10, 4, 10, 0, 0, TimeSpan.FromHours(-6)));

        private static InvoicePdfModel Factura(PdfLogo? logo) => new(
            new PdfIssuerInfo("Clínica de Prueba S. de R.L.", "08019999123456",
                "Tegucigalpa", "2200-0000", Logo: logo),
            new PdfBuyerInfo("Juan Pérez", "08011990123456"),
            new PdfFiscalInfo(
                "001-002-01-00000001", "Factura",
                new DateTimeOffset(2026, 8, 25, 15, 30, 0, TimeSpan.FromHours(-6)),
                "A1B2C3-D4E5F6-A7B8C9", "1-500", new DateOnly(2027, 12, 31), false),
            [new PdfInvoiceLine(1, "Consulta general", 1m, 500m, 0m, 500m, "E")],
            new PdfInvoiceTotals(500m, 0m, 0m, 0m, 0m, 0m, 500m, "QUINIENTOS LEMPIRAS CON 00/100"));

        private static string Texto(byte[] pdf) => Encoding.Latin1.GetString(pdf);

        /// <summary>
        /// Cuenta páginas por los objetos <c>/Type /Page</c> del PDF. Burdo, pero suficiente y
        /// estable: lo que se quiere detectar es que el encabezado empuje contenido a otra hoja.
        /// </summary>
        private static int Paginas(byte[] pdf)
        {
            var texto = Texto(pdf);
            var cuenta = 0;
            var i = 0;
            while ((i = texto.IndexOf("/Type /Page", i, StringComparison.Ordinal)) >= 0)
            {
                // "/Type /Pages" es el nodo raíz del árbol, no una página.
                if (i + 11 >= texto.Length || texto[i + 11] != 's')
                {
                    cuenta++;
                }
                i += 11;
            }
            return cuenta;
        }
    }

    // ── Apoyo ───────────────────────────────────────────────────────────────

    /// <summary>Misma convención que el resto de las pruebas del repositorio.</summary>
    private sealed class RelojFijo(DateTimeOffset instante) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instante;
    }

    private sealed class FakeBrandingRepository : IOrganizationBrandingRepository
    {
        public sealed record Guardado(byte[] Bytes, string ContentType, string? FileName, Guid? UpdatedBy);

        public Guardado? Saved { get; private set; }

        public Task<OrganizationLogoContent?> GetLogoAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<OrganizationLogoContent?>(
                Saved is null ? null : new OrganizationLogoContent(
                    Saved.Bytes, Saved.ContentType, DateTimeOffset.UnixEpoch));

        public Task<OrganizationLogoInfo?> GetLogoInfoAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<OrganizationLogoInfo?>(
                Saved is null ? null : new OrganizationLogoInfo(
                    Saved.ContentType, Saved.FileName, Saved.Bytes.Length, DateTimeOffset.UnixEpoch));

        public Task SaveLogoAsync(
            Guid organizationId, byte[] bytes, string contentType, string? fileName,
            Guid? updatedBy, CancellationToken ct = default)
        {
            Saved = new Guardado(bytes, contentType, fileName, updatedBy);
            return Task.CompletedTask;
        }

        public Task<bool> RemoveLogoAsync(Guid organizationId, Guid? updatedBy, CancellationToken ct = default)
        {
            var habia = Saved is not null;
            Saved = null;
            return Task.FromResult(habia);
        }
    }

    /// <summary>PNG real de las dimensiones pedidas. Se genera en vez de incrustar un blob.</summary>
    private static byte[] Png(int width, int height) => Imagen(width, height, img =>
    {
        using var ms = new MemoryStream();
        img.SaveAsPng(ms);
        return ms.ToArray();
    });

    private static byte[] Jpeg(int width, int height) => Imagen(width, height, img =>
    {
        using var ms = new MemoryStream();
        img.SaveAsJpeg(ms);
        return ms.ToArray();
    });

    private static byte[] Gif(int width, int height) => Imagen(width, height, img =>
    {
        using var ms = new MemoryStream();
        img.SaveAsGif(ms);
        return ms.ToArray();
    });

    private static byte[] Imagen(int width, int height, Func<Image<Rgba32>, byte[]> guardar)
    {
        using var img = new Image<Rgba32>(width, height);

        // Unos píxeles de color: un PNG de un solo tono se comprime tanto que las
        // comparaciones de tamaño pierden sentido.
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                img[x, y] = new Rgba32((byte)(x % 256), (byte)(y % 256), (byte)((x + y) % 256));
            }
        }

        return guardar(img);
    }
}
