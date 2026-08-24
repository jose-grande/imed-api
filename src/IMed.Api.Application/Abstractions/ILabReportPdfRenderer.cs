namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Renderer del <b>informe de resultados</b> — el papel que se lleva el paciente y lee el médico.
/// Mismo patrón que los renderers de orden y receta: el handler arma el modelo y delega.
///
/// <para>No confundir con <see cref="ILabOrderPdfRenderer"/>, que imprime lo que se <i>pide</i>.
/// Este imprime lo que se <i>encontró</i>, y es el único documento del laboratorio que sale de la
/// institución hacia un tercero.</para>
/// </summary>
public interface ILabReportPdfRenderer
{
    byte[] Render(LabReportPdfModel model);
}

/// <summary>Modelo de presentación del informe de resultados.</summary>
public sealed record LabReportPdfModel(
    PdfOrgInfo Organization,
    PdfPatientInfo Patient,
    /// <summary>Agrupaciones del informe: un panel con sus analitos, o pruebas sueltas.</summary>
    IReadOnlyList<PdfReportGroup> Groups,
    /// <summary>
    /// <c>true</c> solo si el informe está liberado. Cuando es <c>false</c> el documento sale con
    /// un aviso imposible de ignorar: un preliminar impreso sin marcar puede terminar en una
    /// historia clínica como si fuera definitivo.
    /// </summary>
    bool IsFinal,
    string? Requisition = null,
    string? AccessionNumber = null,
    string? OrderNumber = null,
    DateTimeOffset? CollectedAt = null,
    DateTimeOffset? ReceivedAt = null,
    DateTimeOffset? ReleasedAt = null,
    DateTimeOffset PrintedAt = default,
    /// <summary>Médico que solicitó el estudio.</summary>
    PdfPractitionerInfo? RequestedBy = null,
    /// <summary>Profesional que validó y liberó. Es la firma del informe.</summary>
    PdfPractitionerInfo? ValidatedBy = null,
    string? Conclusion = null,
    /// <summary>Laboratorio externo, cuando el estudio se derivó.</summary>
    string? ExternalLabName = null);

/// <summary>
/// Un bloque del informe. Para un panel, <see cref="Title"/> es el nombre del panel y los
/// analitos van adentro: el "grouper" de FHIR no tiene valor propio, así que imprimirlo como
/// una fila más dejaba una línea vacía en el papel.
/// </summary>
public sealed record PdfReportGroup(
    string Title,
    string? LoincCode,
    IReadOnlyList<PdfReportLine> Lines);

/// <summary>Una determinación impresa.</summary>
public sealed record PdfReportLine(
    string Name,
    string? LoincCode,
    /// <summary>Valor ya formateado (numérico o textual).</summary>
    string Value,
    string? Unit,
    /// <summary>Rango de referencia legible, ej. "70 – 100".</summary>
    string? ReferenceRange,
    /// <summary>Código de flag (H/L/HH/LL/A/N). Null si no aplica.</summary>
    string? FlagCode,
    /// <summary>Etiqueta legible del flag, ej. "Alto".</summary>
    string? FlagLabel,
    /// <summary>El valor está fuera de rango: se marca en negrita y con símbolo, no solo con color.</summary>
    bool IsAbnormal,
    bool IsCritical,
    /// <summary>El resultado fue rectificado después de emitirse (<c>corrected</c> en FHIR).</summary>
    bool IsCorrected);
