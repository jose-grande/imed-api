using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Organizations;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Prescriptions;

/// <summary>
/// Query del endpoint <c>GET /api/prescriptions/{id}/pdf</c> (legacy: una receta
/// = un MedicationRequest). El handler resuelve todo (MedicationRequest +
/// Patient + Practitioner + Organization + specialty) y delega al
/// <see cref="IPrescriptionPdfRenderer"/>.
///
/// <para>La partition aísla el lookup — un user solo puede generar PDFs de
/// recetas de su propia organización.</para>
/// </summary>
public sealed record GetPrescriptionPdfQuery(
    Guid OrganizationId,
    string MedicationRequestId);

/// <summary>
/// Query del endpoint <c>GET /api/prescriptions/group/{value}/pdf</c>. Una
/// "receta" es el conjunto de MedicationRequests que comparten el mismo
/// <c>groupIdentifier</c> (multi-medicamento). El PDF lista los N medicamentos
/// del grupo bajo un solo encabezado, con las indicaciones generales de la receta.
/// </summary>
public sealed record GetPrescriptionGroupPdfQuery(
    Guid OrganizationId,
    string GroupValue);

public sealed record GetPrescriptionPdfResult(
    byte[] Bytes,
    string FileName);

public sealed class GetPrescriptionPdfHandler(
    IHapiFhirClient hapi,
    IOrganizationProfileRepository orgRepo,
    IOrganizationBrandingRepository branding,
    IUserPractitionerAssignmentRepository assignmentRepo,
    ISpecialtyRepository specialtyRepo,
    IPrescriptionPdfRenderer renderer,
    ILogger<GetPrescriptionPdfHandler> logger)
{
    // Estados que NO se imprimen en una receta agrupada.
    private static readonly HashSet<string> ExcludedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "cancelled", "entered-in-error" };

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

        var context = await ResolvePdfContextAsync(
            medRequest, query.OrganizationId, partitionName, cancellationToken);
        if (context is null)
        {
            return null;
        }

        // Receta de un solo medicamento: las indicaciones del fármaco preferimos
        // patientInstruction; si es legacy (estaban en note), caemos a Note.
        var item = BuildMedicationItem(medRequest, fallbackIndicationsToNote: true);
        var model = new PrescriptionPdfModel(
            Organization: context.Org,
            Practitioner: context.Practitioner,
            Patient: context.Patient,
            Items: new[] { item },
            IssuedAt: medRequest.AuthoredOn ?? DateTimeOffset.UtcNow);

        var bytes = renderer.Render(model);
        return new GetPrescriptionPdfResult(bytes, BuildFileName(model));
    }

    public async Task<GetPrescriptionPdfResult?> HandleGroupAsync(
        GetPrescriptionGroupPdfQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.GroupValue))
        {
            return null;
        }

        var partitionName = $"org-{query.OrganizationId}";

        var members = await hapi.SearchMedicationRequestsByGroupAsync(
            query.GroupValue, partitionName, cancellationToken);

        // Excluimos canceladas/erradas y ordenamos por authoredOn para que el
        // orden del Rp. sea estable (orden en que se agregaron a la receta).
        var active = members
            .Where(m => !ExcludedStatuses.Contains(m.Status))
            .OrderBy(m => m.AuthoredOn ?? DateTimeOffset.MaxValue)
            .ToList();

        if (active.Count == 0)
        {
            return null;
        }

        var representative = active[0];
        var context = await ResolvePdfContextAsync(
            representative, query.OrganizationId, partitionName, cancellationToken);
        if (context is null)
        {
            return null;
        }

        // Las indicaciones generales se replican en el note[] de cada miembro
        // del grupo; tomamos la primera no vacía.
        var generalIndications = active
            .Select(m => m.Note)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));

        var items = active
            .Select(m => BuildMedicationItem(m, fallbackIndicationsToNote: false))
            .ToList();

        var model = new PrescriptionPdfModel(
            Organization: context.Org,
            Practitioner: context.Practitioner,
            Patient: context.Patient,
            Items: items,
            IssuedAt: representative.AuthoredOn ?? DateTimeOffset.UtcNow,
            GeneralIndications: generalIndications);

        var bytes = renderer.Render(model);
        return new GetPrescriptionPdfResult(bytes, BuildFileName(model));
    }

    /// <summary>
    /// Resuelve Organization + Patient + Practitioner + specialty a partir de un
    /// MedicationRequest representativo (todos los miembros de una receta
    /// comparten paciente/encuentro/solicitante). Devuelve null si falta org o
    /// paciente — el PDF sin esos datos no tiene valor.
    /// </summary>
    private async Task<PdfContext?> ResolvePdfContextAsync(
        MedicationRequestSnapshot med,
        Guid organizationId,
        string partitionName,
        CancellationToken cancellationToken)
    {
        var orgProfile = await orgRepo.GetByIdAsync(organizationId, cancellationToken);
        if (orgProfile is null)
        {
            logger.LogWarning(
                "PDF receta {Id}: organization {OrgId} no encontrada localmente.",
                med.Id, organizationId);
            return null;
        }

        PatientSnapshot? patientSnapshot = null;
        if (!string.IsNullOrWhiteSpace(med.PatientFhirId))
        {
            patientSnapshot = await hapi.GetPatientAsync(
                med.PatientFhirId, partitionName, cancellationToken);
        }
        if (patientSnapshot is null)
        {
            logger.LogWarning(
                "PDF receta {Id}: Patient {PatientId} no encontrado.",
                med.Id, med.PatientFhirId);
            return null;
        }

        // Practitioner (mejor esfuerzo). Si no hay requester, mostramos un
        // placeholder — la receta sigue siendo legible.
        PractitionerSnapshot? practitionerSnapshot = null;
        string? specialtyDisplay = null;
        if (!string.IsNullOrWhiteSpace(med.RequesterPractitionerId))
        {
            try
            {
                practitionerSnapshot = await hapi.GetPractitionerAsync(
                    med.RequesterPractitionerId, partitionName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "PDF receta {Id}: no se pudo leer Practitioner {PractitionerId}.",
                    med.Id, med.RequesterPractitionerId);
            }

            // Specialty desde user_practitioner_assignments → PractitionerRole.
            // El display se re-hidrata desde el catálogo local para que aparezca
            // en español aunque HAPI tenga código viejo.
            var assignment = await assignmentRepo.GetByPractitionerFhirIdAsync(
                med.RequesterPractitionerId,
                organizationId,
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
                        med.Id, assignment.PractitionerRoleFhirId);
                }
            }
        }

        var logo = await branding.GetLogoAsync(organizationId, cancellationToken);
        var orgInfo = new PdfOrgInfo(
            Name: orgProfile.Name, TaxId: null, Logo: logo?.ToPdfLogo(organizationId));

        var practName = practitionerSnapshot is not null
            ? string.Join(" ", practitionerSnapshot.Given.Append(practitionerSnapshot.Family ?? string.Empty)).Trim()
            : "Médico responsable";
        var practInfo = new PdfPractitionerInfo(
            DisplayName: string.IsNullOrWhiteSpace(practName) ? "Médico responsable" : practName,
            Prefix: practitionerSnapshot?.Prefix,
            LicenseNumber: practitionerSnapshot?.LicenseNumber,
            Specialty: specialtyDisplay);

        var patientInfo = new PdfPatientInfo(
            DisplayName: patientSnapshot.DisplayName ?? "(sin nombre)",
            BirthDate: patientSnapshot.BirthDate,
            Gender: patientSnapshot.Gender,
            IdentifierLabel: patientSnapshot.IdentifierLabel,
            IdentifierValue: patientSnapshot.IdentifierValue);

        return new PdfContext(orgInfo, patientInfo, practInfo);
    }

    private static PdfMedicationItem BuildMedicationItem(
        MedicationRequestSnapshot med, bool fallbackIndicationsToNote)
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

        // Indicaciones por medicamento: patientInstruction (nuevo). Para recetas
        // legacy de 1 medicamento, las indicaciones estaban en note → fallback.
        var indications = med.PatientInstruction;
        if (string.IsNullOrWhiteSpace(indications) && fallbackIndicationsToNote)
        {
            indications = med.Note;
        }

        return new PdfMedicationItem(
            Medication: medicationLabel,
            Dosage: med.DosageText,
            Route: med.Route,
            Quantity: quantity,
            Indications: indications);
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

    private sealed record PdfContext(
        PdfOrgInfo Org,
        PdfPatientInfo Patient,
        PdfPractitionerInfo Practitioner);
}
