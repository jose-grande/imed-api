using PdfSharpCore.Fonts;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Helper compartido por los renderers PdfSharpCore (recetas K5, órdenes de
/// laboratorio L5, futuros documentos): inicializa el font resolver embebido
/// una sola vez. El resolver es estático global en PdfSharpCore — el ctor
/// del renderer llama <see cref="Ensure"/> idempotentemente.
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
            _initialized = true;
        }
    }
}
