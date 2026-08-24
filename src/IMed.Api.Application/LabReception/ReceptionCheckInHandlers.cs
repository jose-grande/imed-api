using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Check-in de una orden (F2): el paciente solicitó hacerse los exámenes en la
/// clínica → la orden entra a la cola de recepción. Devuelve qué exámenes quedan
/// en cola (in-house) y cuáles hay que referir a un laboratorio externo
/// (no ofertados en la sede). Idempotente.
/// </summary>
public sealed class CheckInOrderHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops,
    ResolveActiveLocationHandler activeLocation, TimeProvider timeProvider)
{
    private static readonly HashSet<string> ExcludedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "revoked", "entered-in-error" };

    public async Task<CheckInResult?> HandleAsync(Guid orgId, string requisition, Guid userId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = (await hapi.SearchLabOrdersByRequisitionAsync(requisition, partition, ct))
            .Where(o => !ExcludedStatuses.Contains(o.Status))
            .ToList();
        if (orders.Count == 0)
        {
            return null;
        }

        // La sede del ingreso decide en qué cola de recepción aparece la orden.
        var locationId = await activeLocation.HandleAsync(orgId, userId, null, ct);
        await ops.CheckInOrderAsync(
            LabOrderCheckIn.Create(orgId, requisition, userId, locationId, timeProvider), ct);

        var patientId = orders[0].PatientFhirId;
        var encounterId = orders[0].EncounterFhirId;
        string? patientName = null;
        if (!string.IsNullOrWhiteSpace(patientId))
        {
            var p = await hapi.GetPatientAsync(patientId, partition, ct);
            patientName = p?.DisplayName;
        }

        var queued = new List<CheckInExam>();
        var external = new List<CheckInExam>();
        foreach (var o in orders)
        {
            var tests = await LabRouting.ExpandOrderAsync(catalog, orgId, o.CatalogCode, ct);
            var inHouse = tests.Count > 0 && tests.Any(t => t.Routing == OrderRouting.InHouse);
            var exam = new CheckInExam(o.Id, o.LoincCode, o.CodeText);
            if (inHouse)
            {
                queued.Add(exam);
            }
            else
            {
                external.Add(exam);
                // F3: el examen no es realizable en la sede → se refiere a externo
                // (trazabilidad). Idempotente: solo si no tiene intake.
                var existing = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
                if (existing is null)
                {
                    var intake = OrderIntake.Create(
                        orgId, o.Id, o.PatientFhirId, OrderRouting.SendOut,
                        LabRouting.ParsePriority(o.Priority), fastingRequired: false, timeProvider: timeProvider);
                    intake.Classify(requisition, null, null, timeProvider);
                    intake.DescribeExam(o.LoincCode, o.CodeText, timeProvider);
                    intake.MarkReferred(userId, timeProvider);
                    await ops.AddIntakeAsync(intake, ct);
                }
            }
        }

        return new CheckInResult(requisition, patientId, patientName, encounterId, queued, external);
    }
}

public sealed record CheckInResult(
    string Requisition,
    string PatientFhirId,
    string? PatientName,
    string? EncounterId,
    IReadOnlyList<CheckInExam> Queued,
    IReadOnlyList<CheckInExam> External);

public sealed record CheckInExam(string ServiceRequestId, string? LoincCode, string? CodeText);

/// <summary>
/// Refiere toda una orden a un laboratorio externo (F3): el paciente decidió no
/// hacérsela en la clínica. Marca como <c>Referred</c> los exámenes que aún no
/// tienen intake (idempotente). Devuelve cuántos se refirieron, o null si la
/// orden no existe.
/// </summary>
public sealed class ReferOrderExternalHandler(
    IHapiFhirClient hapi, ILabOperationsRepository ops, TimeProvider timeProvider)
{
    private static readonly HashSet<string> ExcludedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "revoked", "entered-in-error" };

    public async Task<int?> HandleAsync(Guid orgId, string requisition, Guid userId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = (await hapi.SearchLabOrdersByRequisitionAsync(requisition, partition, ct))
            .Where(o => !ExcludedStatuses.Contains(o.Status))
            .ToList();
        if (orders.Count == 0)
        {
            return null;
        }
        var referred = 0;
        foreach (var o in orders)
        {
            var existing = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
            if (existing is not null)
            {
                continue; // ya recibido / referido / rechazado
            }
            var intake = OrderIntake.Create(
                orgId, o.Id, o.PatientFhirId, OrderRouting.SendOut,
                LabRouting.ParsePriority(o.Priority), fastingRequired: false, timeProvider: timeProvider);
            intake.Classify(requisition, null, null, timeProvider);
            intake.DescribeExam(o.LoincCode, o.CodeText, timeProvider);
            intake.MarkReferred(userId, timeProvider);
            await ops.AddIntakeAsync(intake, ct);
            referred++;
        }
        return referred;
    }
}
