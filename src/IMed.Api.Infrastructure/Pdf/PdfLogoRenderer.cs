using IMed.Api.Application.Abstractions;
using MigraDocCore.DocumentObjectModel;
using MigraDocCore.DocumentObjectModel.MigraDoc.DocumentObjectModel.Shapes;
// `Image` y `ImageSource` viven en namespaces distintos dentro del mismo paquete: el primero en
// `...DocumentObjectModel.Shapes`, el segundo en `...DocumentObjectModel.MigraDoc.DocumentObjectModel.Shapes`.
using MigraDocCore.DocumentObjectModel.Shapes;
using MigraDocCore.DocumentObjectModel.Tables;

namespace IMed.Api.Infrastructure.Pdf;

/// <summary>
/// Dibuja el logo de la organización en el encabezado de un documento.
/// </summary>
/// <remarks>
/// <para>Observación de QA (ronda 1, staging): «necesitamos agregarle el formato con logo de la
/// clínica». Son cuatro documentos —informe de laboratorio, orden de exámenes, receta y factura—
/// y conviene que el logo se vea igual en los cuatro, así que el encaje vive acá y no repetido en
/// cada renderer.</para>
///
/// <para><b>El logo se encaja en una caja, no se escala a un ancho fijo.</b> Un logo apaisado y
/// uno cuadrado tienen proporciones muy distintas: fijar solo el ancho hace que el cuadrado
/// empuje el encabezado hacia abajo hasta descolocar todo lo demás. Fijar ancho y alto a la vez
/// lo deformaría. La salida es mirar las dimensiones reales en píxeles —que
/// <c>IImageSource</c> expone— y ajustar la dimensión que se pase primero del límite.</para>
/// </remarks>
internal static class PdfLogoRenderer
{
    /// <summary>Ancho máximo del logo en los documentos tamaño carta.</summary>
    public static readonly Unit DefaultMaxWidth = Unit.FromCentimeter(3.2);

    /// <summary>
    /// Alto máximo. Acota cuánto puede crecer el encabezado: por encima de esto, un logo
    /// cuadrado o vertical empezaría a desplazar los datos del paciente.
    /// </summary>
    public static readonly Unit DefaultMaxHeight = Unit.FromCentimeter(1.8);

    /// <summary>
    /// Agrega el logo a una celda del encabezado, encajado en la caja indicada.
    /// </summary>
    /// <returns>
    /// <c>true</c> si el logo quedó dibujado. <c>false</c> si no había logo o si los bytes no se
    /// pudieron decodificar — en ese caso el documento sale sin logo, que es muy preferible a no
    /// poder emitirlo. Un informe de laboratorio no se cae por un archivo de imagen.
    /// </returns>
    public static bool TryDraw(Cell cell, PdfLogo? logo, Unit? maxWidth = null, Unit? maxHeight = null)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (logo is null || logo.Bytes.Length == 0)
        {
            return false;
        }

        try
        {
            // El nombre es la clave de caché de MigraDoc, que es por proceso. Con un nombre
            // fijo, la segunda organización que emitiera un documento recibiría el logo de la
            // primera: de ahí que el handler construya una clave por organización y versión.
            var source = ImageSource.FromBinary(logo.CacheKey, () => logo.Bytes);

            var image = cell.AddImage(source);
            Fit(image, source, maxWidth ?? DefaultMaxWidth, maxHeight ?? DefaultMaxHeight);
            return true;
        }
        catch (Exception)
        {
            // Bytes corruptos, un formato que ImageSharp no reconoce, o una imagen tan grande
            // que no se puede decodificar. El documento sigue.
            return false;
        }
    }

    /// <summary>
    /// Igual que la sobrecarga de celda, para los documentos cuyo encabezado es una secuencia de
    /// párrafos y no una tabla — la factura.
    /// </summary>
    /// <param name="alignment">
    /// Alineación del párrafo del logo. El ticket térmico lo centra; la factura carta lo deja a
    /// la izquierda, con el resto de los datos del emisor.
    /// </param>
    public static bool TryDraw(
        Section section,
        PdfLogo? logo,
        ParagraphAlignment alignment = ParagraphAlignment.Left,
        Unit? maxWidth = null,
        Unit? maxHeight = null)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (logo is null || logo.Bytes.Length == 0)
        {
            return false;
        }

        try
        {
            var source = ImageSource.FromBinary(logo.CacheKey, () => logo.Bytes);
            var paragraph = section.AddParagraph();
            paragraph.Format.Alignment = alignment;
            var image = paragraph.AddImage(source);
            Fit(image, source, maxWidth ?? DefaultMaxWidth, maxHeight ?? DefaultMaxHeight);
            return true;
        }
        catch (Exception)
        {
            // Acá el párrafo puede haber quedado agregado y vacío: cuesta un renglón en blanco,
            // que es menos grave que no poder emitir la factura.
            return false;
        }
    }

    /// <summary>
    /// Fija una sola dimensión —la que se pasa primero de su límite— y deja que MigraDoc
    /// derive la otra con <c>LockAspectRatio</c>.
    /// </summary>
    private static void Fit(Image image, ImageSource.IImageSource source, Unit maxWidth, Unit maxHeight)
    {
        image.LockAspectRatio = true;

        // Leer Width/Height fuerza la decodificación; si los bytes no son una imagen, acá es
        // donde revienta, y lo atrapa TryDraw.
        var pixelWidth = source.Width;
        var pixelHeight = source.Height;

        if (pixelWidth <= 0 || pixelHeight <= 0)
        {
            image.Width = maxWidth;
            return;
        }

        // ¿Qué límite se alcanza primero? Comparar proporciones evita convertir unidades.
        var imageRatio = (double)pixelHeight / pixelWidth;
        var boxRatio = maxHeight.Point / maxWidth.Point;

        if (imageRatio > boxRatio)
        {
            // Más alta que la caja: el alto es el límite que manda.
            image.Height = maxHeight;
        }
        else
        {
            image.Width = maxWidth;
        }
    }
}
