using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabOrders;

/// <summary>
/// Query del endpoint <c>GET /api/lab-orders/{id}/pdf</c> (Épica L5). Mismo
/// patrón que <c>GetPrescriptionPdfHandler</c> (K5): resuelve todo y delega
/// al renderer. La partition aísla el lookup.
/// </summary>
public sealed record GetLabOrderPdfQuery(
    Guid OrganizationId,
    string ServiceRequestId);

public sealed record GetLabOrderPdfResult(
    byte[] Bytes,
    string FileName);

public sealed class GetLabOrderPdfHandler(
    IHapiFhirClient hapi,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    ISpecialtyRepository specialtyRepo,
    ILabOrderPdfRenderer renderer,
    ILogger<GetLabOrderPdfHandler> logger)
{
    public async Task<GetLabOrderPdfResult?> HandleAsync(
        GetLabOrderPdfQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.ServiceRequestId))
        {
            return null;
        }

        var partitionName = $"org-{query.OrganizationId}";

        var order = await hapi.GetLabOrderAsync(query.ServiceRequestId, partitionName, cancellationToken);
        if (order is null)
        {
            return null;
        }

        var orgProfile = await orgRepo.GetByIdAsync(query.OrganizationId, cancellationToken);
        if (orgProfile is null)
        {
            logger.LogWarning(
                "PDF lab order {Id}: organization {OrgId} no encontrada localmente.",
                query.ServiceRequestId, query.OrganizationId);
            return null;
        }

        PatientSnapshot? patientSnapshot = null;
        if (!string.IsNullOrWhiteSpace(order.PatientFhirId))
        {
            patientSnapshot = await hapi.GetPatientAsync(order.PatientFhirId, partitionName, cancellationToken);
        }
        if (patientSnapshot is null)
        {
            logger.LogWarning(
                "PDF lab order {Id}: Patient {PatientId} no encontrado.",
                query.ServiceRequestId, order.PatientFhirId);
            return null;
        }

        PractitionerSnapshot? practitionerSnapshot = null;
        string? specialtyDisplay = null;
        if (!string.IsNullOrWhiteSpace(order.RequesterPractitionerId))
        {
            try
            {
                practitionerSnapshot = await hapi.GetPractitionerAsync(
                    order.RequesterPractitionerId, partitionName, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "PDF lab order {Id}: no se pudo leer Practitioner {PractitionerId}.",
                    query.ServiceRequestId, order.RequesterPractitionerId);
            }

            var assignment = await assignmentRepo.GetByPractitionerFhirIdAsync(
                order.RequesterPractitionerId,
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
                        "PDF lab order {Id}: no se pudo leer PractitionerRole {RoleId}.",
                        query.ServiceRequestId, assignment.PractitionerRoleFhirId);
                }
            }
        }

        var model = BuildModel(orgProfile, patientSnapshot, practitionerSnapshot, specialtyDisplay, order);
        var bytes = renderer.Render(model);
        var fileName = BuildFileName(model);
        return new GetLabOrderPdfResult(bytes, fileName);
    }

    private static LabOrderPdfModel BuildModel(
        OrganizationProfile orgProfile,
        PatientSnapshot patient,
        PractitionerSnapshot? practitioner,
        string? specialtyDisplay,
        LabOrderSnapshot order)
    {
        var orgInfo = new PdfOrgInfo(orgProfile.Name, TaxId: null);

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

        var examLabel = order.CodeText
            ?? order.CodeDisplay
            ?? order.LoincCode
            ?? "(examen sin nombre)";

        string? occurrenceLabel = null;
        if (!string.IsNullOrWhiteSpace(order.OccurrenceDateTime)
            && DateTimeOffset.TryParse(order.OccurrenceDateTime, out var oc))
        {
            occurrenceLabel = oc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        }

        var orderItem = new PdfLabOrderItem(
            ExamLabel: examLabel,
            LoincCode: order.LoincCode,
            PriorityLabel: FormatPriority(order.Priority),
            OccurrenceLabel: occurrenceLabel,
            Reason: order.ReasonText,
            Notes: order.Note);

        return new LabOrderPdfModel(
            Organization: orgInfo,
            Practitioner: practInfo,
            Patient: patientInfo,
            Order: orderItem,
            IssuedAt: order.AuthoredOn ?? DateTimeOffset.UtcNow);
    }

    private static string? FormatPriority(string? priority) => priority switch
    {
        null => null,
        "routine" => "Rutina",
        "urgent" => "Urgente",
        "asap" => "Tan pronto como sea posible",
        "stat" => "STAT",
        _ => priority,
    };

    private static string BuildFileName(LabOrderPdfModel model)
    {
        var patient = Sanitize(model.Patient.DisplayName);
        var exam = Sanitize(model.Order.ExamLabel);
        var date = model.IssuedAt.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"orden_lab_{exam}_{patient}_{date}.pdf";
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "documento";
        }
        var chars = value
            .Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')
            .ToArray();
        var sanitized = new string(chars);
        return string.IsNullOrEmpty(sanitized) ? "documento" : sanitized;
    }
}
