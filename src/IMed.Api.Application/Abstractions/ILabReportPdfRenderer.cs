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

/// <summary>
/// Modelo de presentación del informe de resultados <b>de una orden</b>: todos sus paneles y
/// pruebas individuales en un solo documento, organizados por área y subárea.
/// </summary>
public sealed record LabReportPdfModel(
    PdfOrgInfo Organization,
    PdfPatientInfo Patient,
    /// <summary>
    /// Áreas con resultados validados, en el orden del informe. Un área, bloque o subárea sin
    /// resultados no llega acá: el informe no imprime títulos vacíos.
    /// </summary>
    IReadOnlyList<PdfReportArea> Areas,
    /// <summary>
    /// Paneles o pruebas de la orden todavía sin validar. No se imprimen; el informe solo avisa
    /// que hay pendientes, para que nadie lo tome por completo.
    /// </summary>
    int PendingCount = 0,
    string? Requisition = null,
    DateTimeOffset? ReceivedAt = null,
    DateTimeOffset? ReleasedAt = null,
    DateTimeOffset PrintedAt = default,
    /// <summary>Médico que solicitó el estudio.</summary>
    PdfPractitionerInfo? RequestedBy = null,
    /// <summary>Profesionales que validaron lo impreso (sin repetir). Son las firmas del informe.</summary>
    IReadOnlyList<PdfPractitionerInfo>? Validators = null,
    /// <summary>Laboratorio externo, cuando algún estudio se derivó.</summary>
    string? ExternalLabName = null);

/// <summary>Un área del laboratorio en el informe (Hematología, Uroanálisis…).</summary>
public sealed record PdfReportArea(string Title, IReadOnlyList<PdfReportBlock> Blocks);

/// <summary>
/// Un panel (con <see cref="Title"/> = nombre del panel) o las pruebas individuales del área
/// (<see cref="Title"/> null). El "grouper" de FHIR no tiene valor propio: es el título del bloque,
/// no una fila.
/// </summary>
public sealed record PdfReportBlock(
    string? Title,
    /// <summary>Quién validó el panel ("Resultados validados por"). Null en pruebas individuales.</summary>
    string? ValidatedBy,
    IReadOnlyList<PdfReportSubsection> Subsections,
    /// <summary>Comentario del validador al liberar.</summary>
    string? Conclusion = null);

/// <summary>Subárea del bloque (Serie roja, Examen físico…). <see cref="Title"/> null = sin subárea.</summary>
public sealed record PdfReportSubsection(string? Title, IReadOnlyList<PdfReportLine> Lines);

/// <summary>Una determinación impresa.</summary>
public sealed record PdfReportLine(
    string Name,
    /// <summary>Valor ya formateado (numérico o textual).</summary>
    string Value,
    string? Unit,
    /// <summary>Rango de referencia legible, ej. "70 – 100".</summary>
    string? ReferenceRange,
    /// <summary>
    /// "*" fuera de rango, "**" valor crítico, "" normal. Es la <b>única</b> señal: sin color, sin
    /// negrita y sin palabras como "Crítico alto", por la sensibilidad del paciente que lee el
    /// informe (observación del cliente).
    /// </summary>
    string Mark,
    /// <summary>El resultado fue rectificado después de emitirse (<c>corrected</c> en FHIR).</summary>
    bool IsCorrected);
