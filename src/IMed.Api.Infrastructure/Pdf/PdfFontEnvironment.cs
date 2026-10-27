using MigraDocCore.DocumentObjectModel.MigraDoc.DocumentObjectModel.Shapes;
using PdfSharpCore.Fonts;
using SixLabors.ImageSharp.PixelFormats;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Helper compartido por los renderers PdfSharpCore (recetas K5, órdenes de
/// laboratorio L5, futuros documentos): inicializa el font resolver embebido
/// una sola vez. El resolver es estático global en PdfSharpCore — el ctor
/// del renderer llama <see cref="Ensure"/> idempotentemente.
///
/// <para>Desde la observación de QA del logo de la clínica, también registra el decodificador
/// de imágenes de MigraDoc, que es otro estático global del mismo tipo.</para>
/// </summary>
internal static class PdfFontEnvironment
{
    private static readonly object Lock = new();
    private static bool _initialized;

    public static void Ensure()
    {
        if (_initialized)
        {
            return;
        }
        lock (Lock)
        {
            if (_initialized)
            {
                return;
            }
            // PdfSharpCore lazy-resolvea cuando no hay resolver explícito en
            // Windows; para Linux/Docker forzamos el resolver embebido (Arial
            // sintético desde recursos del paquete).
            if (GlobalFontSettings.FontResolver is null)
            {
                GlobalFontSettings.FontResolver = new PdfSharpCore.Utils.FontResolver();
            }

            // Sin esto, `ImageSource.FromBinary` lanza NullReferenceException al renderizar:
            // MigraDocCore declara la interfaz pero deja la implementación afuera. La que sirve
            // viaja dentro de PdfSharpCore (`PdfSharpCore.Utils.ImageSharpImageSource<T>`), así
            // que no hace falta ningún paquete nuevo.
            ImageSource.ImageSourceImpl ??= new PdfSharpCore.Utils.ImageSharpImageSource<Rgba32>();

            _initialized = true;
        }
    }
}
