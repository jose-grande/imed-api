namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Renderer del PDF de etiqueta de muestra (R6-F4b). Mismo patrón de puerto que los
/// renderers de receta y orden — el handler arma el modelo y delega.
///
/// <para>A diferencia de aquellos, la página <b>no es A4</b>: cada etiqueta se emite con
/// el tamaño físico exacto de su formato, para que la impresora térmica (o la láser con
/// hoja de etiquetas) la deposite sin escalar. Un PDF puede llevar varias etiquetas
/// (una por página), y páginas de <b>distinto tamaño</b> si los contenedores del plan
/// usan rollos distintos.</para>
/// </summary>
public interface ISpecimenLabelPdfRenderer
{
    byte[] Render(SpecimenLabelSheet sheet);
}

/// <summary>Conjunto de etiquetas a emitir en un solo PDF (una por página).</summary>
public sealed record SpecimenLabelSheet(IReadOnlyList<SpecimenLabelPage> Labels);

/// <summary>
/// Una etiqueta: su formato físico y el contenido a imprimir. Los campos opcionales
/// se omiten cuando no hay dato o cuando el tamaño no da para mostrarlos — la decisión
/// de qué entra la toma el renderer según el área disponible.
/// </summary>
public sealed record SpecimenLabelPage(
    SpecimenLabelFormat Format,
    /// <summary>Número de accession. Es el texto legible y el contenido del código de barras.</summary>
    string Accession,
    /// <summary>Contenido del Code128. Normalmente igual al accession.</summary>
    string BarcodeValue,
    string? PatientName,
    string? PatientBirthDate,
    /// <summary>Tubo/recipiente + aditivo, ej. "Tubo EDTA (tapa lila) · EDTA K2/K3".</summary>
    string? ContainerLabel,
    /// <summary>Orden de extracción dentro del evento de toma (order of draw).</summary>
    int Sequence,
    /// <summary>Volumen requerido en mL, ya calculado por el plan.</summary>
    decimal? RequiredVolumeMl,
    /// <summary>Pruebas que cubre el contenedor, ya concatenadas.</summary>
    string? Tests,
    /// <summary>N.º de orden/requisición legible (ORD-YYYYMMDD-NNNN).</summary>
    string? OrderNumber,
    DateTimeOffset PrintedAt);

/// <summary>Tamaño físico y calibración de la etiqueta, tal como lo configuró el laboratorio.</summary>
public sealed record SpecimenLabelFormat(
    string Name,
    decimal WidthMm,
    decimal HeightMm,
    decimal MarginMm,
    decimal OffsetXMm,
    decimal OffsetYMm);
