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
/// Modelo de presentación para el PDF de orden de laboratorio. La orden
/// referencia un examen único (LOINC o texto libre); el motivo y notas son
/// el contexto clínico para el laboratorio.
/// </summary>
public sealed record LabOrderPdfModel(
    PdfOrgInfo Organization,
    PdfPractitionerInfo Practitioner,
    PdfPatientInfo Patient,
    PdfLabOrderItem Order,
    DateTimeOffset IssuedAt);

public sealed record PdfLabOrderItem(
    string ExamLabel,
    string? LoincCode,
    string? PriorityLabel,
    string? OccurrenceLabel,
    string? Reason,
    string? Notes);
