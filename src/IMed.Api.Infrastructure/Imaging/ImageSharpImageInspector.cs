using IMed.Api.Application.Abstractions;
using SixLabors.ImageSharp;

namespace IMed.Api.Infrastructure.Imaging;

/// <summary>
/// Identifica imágenes con ImageSharp, que ya viaja en el proyecto como dependencia de
/// PdfSharpCore — no hace falta ningún paquete nuevo.
/// </summary>
/// <remarks>
/// Usa <c>Image.Identify</c> y no <c>Image.Load</c>: lee únicamente la cabecera del archivo. Eso
/// da el tipo real y las dimensiones sin asignar el mapa de bits, con lo cual una imagen
/// declarada de 50.000 × 50.000 px no puede agotar la memoria del proceso al validarla.
/// </remarks>
public sealed class ImageSharpImageInspector : IImageInspector
{
    public ImageInspection? Inspect(byte[] bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            // `Identify` devuelve null cuando los bytes no corresponden a ningún formato
            // conocido; no lanza. El try/catch cubre el caso del archivo que empieza como una
            // imagen válida y se corta a la mitad.
            var info = Image.Identify(bytes, out var format);
            if (info is null || format is null)
            {
                return null;
            }

            var contentType = format.DefaultMimeType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return null;
            }

            return new ImageInspection(contentType, info.Width, info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
