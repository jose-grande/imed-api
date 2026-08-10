namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Renderer de PDF de orden de laboratorio (Épica L5). Mismo patrón que el
/// renderer de recetas — el handler arma el modelo y delega.
/// </summary>
public interface ILabOrderPdfRenderer
{
    byte[] Render(LabOrderPdfModel model);
}

/// <summary>
/// Modelo de presentación para el PDF de orden de laboratorio. Una orden puede
/// incluir varios exámenes/paneles (los que comparten <c>requisition</c>); cada
/// uno con su prioridad / fecha sugerida / motivo / notas.
/// </summary>
public sealed record LabOrderPdfModel(
    PdfOrgInfo Organization,
    PdfPractitionerInfo Practitioner,
    PdfPatientInfo Patient,
    IReadOnlyList<PdfLabOrderItem> Items,
    DateTimeOffset IssuedAt,
    // F1: número de orden correlativo legible (ORD-YYYYMMDD-NNNN).
    string? OrderNumber = null);

public sealed record PdfLabOrderItem(
    string ExamLabel,
    string? LoincCode,
    string? PriorityLabel,
    string? OccurrenceLabel,
    string? Reason,
    string? Notes,
    // F1: dónde se puede hacer ("Disponible en clínica" / "Referir a laboratorio externo" / etc.).
    string? AvailabilityLabel = null);
