namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Mira unos bytes y dice si son realmente una imagen, y de qué tipo.
/// </summary>
/// <remarks>
/// <para>Existe por el logo de la organización. Confiar en la extensión del archivo o en el
/// <c>Content-Type</c> que declara el navegador no sirve para nada: los dos los elige quien
/// sube el archivo. Lo único que distingue un PNG de un ejecutable renombrado es el contenido.
/// </para>
///
/// <para>Es un puerto y no una llamada directa porque la biblioteca que sabe hacer esto
/// (ImageSharp) entra por Infrastructure, de la mano de PdfSharpCore.</para>
/// </remarks>
public interface IImageInspector
{
    /// <summary>
    /// Identifica la imagen leyendo solo su cabecera.
    /// </summary>
    /// <returns>
    /// Los datos de la imagen, o <c>null</c> si los bytes no son una imagen reconocible.
    /// </returns>
    ImageInspection? Inspect(byte[] bytes);
}

/// <summary>Lo que se puede saber de una imagen sin decodificarla entera.</summary>
/// <param name="ContentType">Tipo real, deducido del contenido — no de lo que declaró nadie.</param>
/// <param name="Width">Ancho en píxeles.</param>
/// <param name="Height">Alto en píxeles.</param>
public sealed record ImageInspection(string ContentType, int Width, int Height);
