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
    DateTimeOffset IssuedAt,
    // Indicaciones generales de la receta (a nivel de receta, no por medicamento).
    string? GeneralIndications = null);

public sealed record PdfOrgInfo(string Name, string? TaxId, PdfLogo? Logo = null);

/// <summary>
/// Logo de la organización, listo para incrustar en un documento.
/// </summary>
/// <remarks>
/// <para>Observación de QA (ronda 1, staging): «necesitamos agregarle el formato con logo de la
/// clínica». Va en el encabezado del informe de laboratorio, la orden de exámenes, la receta y la
/// factura. <b>No</b> en las etiquetas de muestra: las hay de 33×13 mm, donde el logo competiría
/// con el código de barras.</para>
///
/// <para><see cref="CacheKey"/> existe porque MigraDoc identifica las imágenes por nombre y
/// mantiene una caché por proceso. Con un nombre fijo, la segunda organización que emitiera un
/// documento recibiría el logo de la primera.</para>
/// </remarks>
/// <param name="Bytes">La imagen. PNG o JPEG — el renderizador no sabe incrustar otra cosa.</param>
/// <param name="ContentType">Tipo de la imagen, tal como se cargó.</param>
/// <param name="CacheKey">
/// Identificador estable y único por imagen. El handler usa <c>{organizationId}-{updatedAt}</c>:
/// único entre organizaciones, y cambia cuando se reemplaza el logo.
/// </param>
public sealed record PdfLogo(byte[] Bytes, string ContentType, string CacheKey);

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
