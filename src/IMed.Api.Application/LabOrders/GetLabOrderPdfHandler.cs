using System.Globalization;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabOrders;

/// <summary>
/// Query del endpoint <c>GET /api/lab-orders/{id}/pdf</c> (Épica L5, legacy: una
/// orden = un ServiceRequest). Resuelve todo y delega al renderer. La partition
/// aísla el lookup.
/// </summary>
public sealed record GetLabOrderPdfQuery(
    Guid OrganizationId,
    string ServiceRequestId);

/// <summary>
/// Query del endpoint <c>GET /api/lab-orders/group/{requisition}/pdf</c>. Una
/// "orden de laboratorio" es el conjunto de ServiceRequests que comparten el
/// mismo <c>requisition</c> (multi-examen). El PDF lista los N exámenes/paneles.
/// </summary>
public sealed record GetLabOrderGroupPdfQuery(
    Guid OrganizationId,
    string Requisition);

public sealed record GetLabOrderPdfResult(
    byte[] Bytes,
    string FileName);

public sealed class GetLabOrderPdfHandler(
    IHapiFhirClient hapi,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    ISpecialtyRepository specialtyRepo,
    ILabCatalogRepository catalog,
    ILabOrderPdfRenderer renderer,
    ILogger<GetLabOrderPdfHandler> logger)
{
    // Estados que NO se imprimen en una orden agrupada.
    private static readonly HashSet<string> ExcludedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "revoked", "entered-in-error" };

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

        var context = await ResolvePdfContextAsync(order, query.OrganizationId, partitionName, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var model = new LabOrderPdfModel(
            Organization: context.Org,
            Practitioner: context.Practitioner,
            Patient: context.Patient,
            Items: new[] { BuildItem(order, await AvailabilityLabelAsync(query.OrganizationId, order, cancellationToken)) },
            IssuedAt: order.AuthoredOn ?? DateTimeOffset.UtcNow,
            OrderNumber: order.Requisition);

        var bytes = renderer.Render(model);
        return new GetLabOrderPdfResult(bytes, BuildFileName(model));
    }

    public async Task<GetLabOrderPdfResult?> HandleGroupAsync(
        GetLabOrderGroupPdfQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrWhiteSpace(query.Requisition))
        {
            return null;
        }

        var partitionName = $"org-{query.OrganizationId}";

        var members = await hapi.SearchLabOrdersByRequisitionAsync(query.Requisition, partitionName, cancellationToken);
        var active = members
            .Where(m => !ExcludedStatuses.Contains(m.Status))
            .OrderBy(m => m.AuthoredOn ?? DateTimeOffset.MaxValue)
            .ToList();

        if (active.Count == 0)
        {
            return null;
        }

        var representative = active[0];
        var context = await ResolvePdfContextAsync(representative, query.OrganizationId, partitionName, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var items = new List<PdfLabOrderItem>(active.Count);
        foreach (var m in active)
        {
            items.Add(BuildItem(m, await AvailabilityLabelAsync(query.OrganizationId, m, cancellationToken)));
        }

        var model = new LabOrderPdfModel(
            Organization: context.Org,
            Practitioner: context.Practitioner,
            Patient: context.Patient,
            Items: items,
            IssuedAt: representative.AuthoredOn ?? DateTimeOffset.UtcNow,
            OrderNumber: representative.Requisition);

        var bytes = renderer.Render(model);
        return new GetLabOrderPdfResult(bytes, BuildFileName(model));
    }

    private async Task<string?> AvailabilityLabelAsync(Guid orgId, LabOrderSnapshot order, CancellationToken ct)
    {
        var code = await LabAvailability.ClassifyAsync(catalog, orgId, order.CatalogCode, ct);
        return code switch
        {
            LabAvailability.InHouse => "Disponible en la clínica",
            LabAvailability.SendOut => "Referir a laboratorio externo",
            LabAvailability.Partial => "Parcial: algunos exámenes se refieren a externo",
            _ => null,
        };
    }

    /// <summary>
    /// Resuelve Organization + Patient + Practitioner + specialty a partir de una
    /// orden representativa (todos los miembros de una orden comparten
    /// paciente/encuentro/solicitante). Null si falta org o paciente.
    /// </summary>
    private async Task<PdfContext?> ResolvePdfContextAsync(
        LabOrderSnapshot order,
        Guid organizationId,
        string partitionName,
        CancellationToken cancellationToken)
    {
        var orgProfile = await orgRepo.GetByIdAsync(organizationId, cancellationToken);
        if (orgProfile is null)
        {
            logger.LogWarning(
                "PDF lab order {Id}: organization {OrgId} no encontrada localmente.",
                order.Id, organizationId);
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
                order.Id, order.PatientFhirId);
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
                    order.Id, order.RequesterPractitionerId);
            }

            var assignment = await assignmentRepo.GetByPractitionerFhirIdAsync(
                order.RequesterPractitionerId,
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
                        "PDF lab order {Id}: no se pudo leer PractitionerRole {RoleId}.",
                        order.Id, assignment.PractitionerRoleFhirId);
                }
            }
        }

        var orgInfo = new PdfOrgInfo(orgProfile.Name, TaxId: null);

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

    private static PdfLabOrderItem BuildItem(LabOrderSnapshot order, string? availabilityLabel)
    {
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

        return new PdfLabOrderItem(
            ExamLabel: examLabel,
            LoincCode: order.LoincCode,
            PriorityLabel: FormatPriority(order.Priority),
            OccurrenceLabel: occurrenceLabel,
            Reason: order.ReasonText,
            Notes: order.Note,
            AvailabilityLabel: availabilityLabel);
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
        var date = model.IssuedAt.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        return $"orden_lab_{patient}_{date}.pdf";
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

    private sealed record PdfContext(
        PdfOrgInfo Org,
        PdfPatientInfo Patient,
        PdfPractitionerInfo Practitioner);
}
