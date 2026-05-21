namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Renderer de PDF de receta médica (Épica K5). El handler arma el
/// <see cref="PrescriptionPdfModel"/> a partir del MedicationRequest + Patient +
/// Practitioner + Organization y delega al renderer. El endpoint devuelve los
/// bytes con <c>Content-Type: application/pdf</c>.
///
/// <para>El renderer es un puerto sin estado — la implementación con
/// PdfSharpCore + MigraDoc vive en Infrastructure.</para>
/// </summary>
public interface IPrescriptionPdfRenderer
{
    byte[] Render(PrescriptionPdfModel model);
}

/// <summary>
/// Modelo de presentación para el PDF. Aplana todo lo que la plantilla necesita
/// — la receta es un documento snapshot, no consulta nada en runtime.
/// </summary>
public sealed record PrescriptionPdfModel(
    PdfOrgInfo Organization,
    PdfPractitionerInfo Practitioner,
    PdfPatientInfo Patient,
    IReadOnlyList<PdfMedicationItem> Items,
    DateTimeOffset IssuedAt);

public sealed record PdfOrgInfo(string Name, string? TaxId);

public sealed record PdfPractitionerInfo(
    string DisplayName,
    string? Prefix,
    string? LicenseNumber,
    string? Specialty);

public sealed record PdfPatientInfo(
    string DisplayName,
    string? BirthDate,
    string? Gender,
    string? IdentifierLabel,
    string? IdentifierValue);

public sealed record PdfMedicationItem(
    string Medication,
    string? Dosage,
    string? Route,
    string? Quantity,
    string? Indications);
