using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Prescriptions;

/// <summary>
/// Query del endpoint <c>GET /api/prescriptions/{id}/pdf</c>. El handler
/// resuelve todo (MedicationRequest + Patient + Practitioner + Organization +
/// specialty) y delega al <see cref="IPrescriptionPdfRenderer"/>.
///
/// <para>La partition aísla el lookup — un user solo puede generar PDFs de
/// recetas de su propia organización.</para>
/// </summary>
public sealed record GetPrescriptionPdfQuery(
    Guid OrganizationId,
    string MedicationRequestId);

public sealed record GetPrescriptionPdfResult(
    byte[] Bytes,
    string FileName);

public sealed class GetPrescriptionPdfHandler(
    IHapiFhirClient hapi,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    ISpecialtyRepository specialtyRepo,
    IPrescriptionPdfRenderer renderer,
    ILogger<GetPrescriptionPdfHandler> logger)
{
    public async Task<GetPrescriptionPdfResult?> HandleAsync(
        GetPrescriptionPdfQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.MedicationRequestId))
        {
            return null;
        }

        var partitionName = $"org-{query.OrganizationId}";

        var medRequest = await hapi.GetMedicationRequestAsync(
            query.MedicationRequestId, partitionName, cancellationToken);
        if (medRequest is null)
        {
            return null;
        }

        // Organization profile (local DB) — incluye name + tax id si está cargado.
        var orgProfile = await orgRepo.GetByIdAsync(query.OrganizationId, cancellationToken);
        if (orgProfile is null)
        {
            logger.LogWarning(
                "PDF receta {Id}: organization {OrgId} no encontrada localmente.",
                query.MedicationRequestId, query.OrganizationId);
            return null;
        }

        // Patient. Si no se puede leer, fallamos — el PDF sin paciente no tiene valor.
        PatientSnapshot? patientSnapshot = null;
        if (!string.IsNullOrWhiteSpace(medRequest.PatientFhirId))
        {
            patientSnapshot = await hapi.GetPatientAsync(
                medRequest.PatientFhirId, partitionName, cancellationToken);
        }
        if (patientSnapshot is null)
        {
            logger.LogWarning(
                "PDF receta {Id}: Patient {PatientId} no encontrado.",
                query.MedicationRequestId, medRequest.PatientFhirId);
            return null;
        }

        // Practitioner (mejor esfuerzo). Si no hay requester, mostramos un
        // placeholder — la receta sigue siendo legible.
        PractitionerSnapshot? practitionerSnapshot = null;
        string? specialtyDisplay = null;
        if (!string.IsNullOrWhiteSpace(medRequest.RequesterPractitionerId))
        {
            try
            {
                practitionerSnapshot = await hapi.GetPractitionerAsync(
                    medRequest.RequesterPractitionerId, partitionName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "PDF receta {Id}: no se pudo leer Practitioner {PractitionerId}.",
                    query.MedicationRequestId, medRequest.RequesterPractitionerId);
            }

            // Specialty desde user_practitioner_assignments → PractitionerRole.
            // El display se re-hidrata desde el catálogo local para que aparezca
            // en español aunque HAPI tenga código viejo.
            var assignment = await assignmentRepo.GetByPractitionerFhirIdAsync(
                medRequest.RequesterPractitionerId,
                query.OrganizationId,
                cancellationToken);
            if (assignment is not null && !string.IsNullOrEmpty(assignment.PractitionerRoleFhirId))
            {
                try
                {
                    var role = await hapi.GetPractitionerRoleAsync(
                        assignment.PractitionerRoleFhirId, partitionName, cancellationToken);
                    if (!string.IsNullOrWhiteSpace(role?.SpecialtyCode))
                    {
                        var catalog = await specialtyRepo.GetByCodeAsync(role.SpecialtyCode, cancellationToken);
                        specialtyDisplay = catalog?.DisplayEs ?? role.SpecialtyDisplay;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex,
                        "PDF receta {Id}: no se pudo leer PractitionerRole {RoleId}.",
                        query.MedicationRequestId, assignment.PractitionerRoleFhirId);
                }
            }
        }

        var model = BuildModel(orgProfile, patientSnapshot, practitionerSnapshot, specialtyDisplay, medRequest);
        var bytes = renderer.Render(model);

        var fileName = BuildFileName(model);
        return new GetPrescriptionPdfResult(bytes, fileName);
    }

    private static PrescriptionPdfModel BuildModel(
        OrganizationProfile orgProfile,
        PatientSnapshot patient,
        PractitionerSnapshot? practitioner,
        string? specialtyDisplay,
        MedicationRequestSnapshot med)
    {
        var orgInfo = new PdfOrgInfo(
            Name: orgProfile.Name,
            TaxId: null);

        var practName = practitioner is not null
            ? string.Join(" ", practitioner.Given.Append(practitioner.Family ?? string.Empty)).Trim()
            : "Médico responsable";
        var practInfo = new PdfPractitionerInfo(
            DisplayName: string.IsNullOrWhiteSpace(practName) ? "Médico responsable" : practName,
            Prefix: practitioner?.Prefix,
            LicenseNumber: practitioner?.LicenseNumber,
            Specialty: specialtyDisplay);

        var patientInfo = new PdfPatientInfo(
            DisplayName: patient.DisplayName ?? "(sin nombre)",
            BirthDate: patient.BirthDate,
            Gender: patient.Gender,
            IdentifierLabel: patient.IdentifierLabel,
            IdentifierValue: patient.IdentifierValue);

        var item = BuildMedicationItem(med);

        return new PrescriptionPdfModel(
            Organization: orgInfo,
            Practitioner: practInfo,
            Patient: patientInfo,
            Items: new[] { item },
            IssuedAt: med.AuthoredOn ?? DateTimeOffset.UtcNow);
    }

    private static PdfMedicationItem BuildMedicationItem(MedicationRequestSnapshot med)
    {
        // El display preferido es: text libre > display del coding > código.
        var medicationLabel = med.MedicationText
            ?? med.MedicationDisplay
            ?? med.MedicationCode
            ?? "(medicamento)";

        string? quantity = null;
        if (med.DispenseQuantity.HasValue)
        {
            quantity = med.DispenseUnit is null
                ? med.DispenseQuantity.Value.ToString(CultureInfo.InvariantCulture)
                : $"{med.DispenseQuantity.Value.ToString(CultureInfo.InvariantCulture)} {med.DispenseUnit}";
        }

        return new PdfMedicationItem(
            Medication: medicationLabel,
            Dosage: med.DosageText,
            Route: med.Route,
            Quantity: quantity,
            Indications: med.Note);
    }

    private static string BuildFileName(PrescriptionPdfModel model)
    {
        var patient = Sanitize(model.Patient.DisplayName);
        var date = model.IssuedAt.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"receta_{patient}_{date}.pdf";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "paciente";
        }
        var chars = value
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrEmpty(sanitized) ? "paciente" : sanitized;
    }
}
