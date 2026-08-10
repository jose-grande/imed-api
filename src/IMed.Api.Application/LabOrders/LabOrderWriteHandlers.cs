using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabOrders;

/// <summary>
/// Clasifica la disponibilidad de una orden en la clínica (F1): expande
/// panel→pruebas y mira el enrutamiento in-house/send-out de cada una.
/// </summary>
internal static class LabAvailability
{
    public const string InHouse = "in-house";
    public const string SendOut = "send-out";
    public const string Partial = "partial";

    public static async Task<string?> ClassifyAsync(
        ILabCatalogRepository catalog, Guid orgId, string? loincOrPanelCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(loincOrPanelCode))
        {
            return null; // texto libre / sin código → disponibilidad desconocida
        }
        var tests = await LabRouting.ExpandOrderAsync(catalog, orgId, loincOrPanelCode, ct);
        if (tests.Count == 0)
        {
            return SendOut; // no está en el catálogo de la sede → referir a externo
        }
        var inHouse = tests.Count(t => t.Routing == OrderRouting.InHouse);
        if (inHouse == tests.Count)
        {
            return InHouse;
        }
        if (inHouse == 0)
        {
            return SendOut;
        }
        return Partial;
    }
}

/// <summary>
/// DTO de una orden de laboratorio expuesto por el gateway (LIS-RD-09): el médico
/// crea/edita/lista órdenes a través de imed-api (no directo a HAPI), cerrando la
/// deuda de seguridad de que el frontend escribía FHIR con el token del usuario.
/// </summary>
public sealed record LabOrderDto(
    string Id,
    string PatientFhirId,
    string? EncounterFhirId,
    string? RequesterPractitionerId,
    string Status,
    string Intent,
    string? LoincCode,
    string? LocalCode,
    string? CodeText,
    string? CodeDisplay,
    string? Priority,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    string? Requisition,
    DateTimeOffset? AuthoredOn,
    // F1: disponibilidad en la clínica — "in-house" | "send-out" | "partial" | null (desconocida).
    string? Availability = null)
{
    public static LabOrderDto FromSnapshot(LabOrderSnapshot s) => new(
        s.Id, s.PatientFhirId, s.EncounterFhirId, s.RequesterPractitionerId, s.Status, s.Intent,
        s.LoincCode, null, s.CodeText, s.CodeDisplay, s.Priority, s.OccurrenceDateTime,
        s.ReasonText, s.Note, s.Requisition, s.AuthoredOn);
}

/// <summary>Crear (Id null) o actualizar una orden de laboratorio del médico.</summary>
public sealed record UpsertLabOrderCommand(
    Guid OrganizationId,
    string? Id,
    string PatientFhirId,
    string? EncounterFhirId,
    string? RequesterPractitionerId,
    string Status,
    string? Priority,
    string? LoincCode,
    string? LocalCode,
    string? LocalSystem,
    string CodeText,
    string? CodeDisplay,
    string? OccurrenceDateTime,
    string? ReasonText,
    string? Note,
    string? Requisition);

public sealed class UpsertLabOrderHandler(IHapiFhirClient hapi, ILabCatalogRepository catalog)
{
    public async Task<LabOrderDto> HandleAsync(UpsertLabOrderCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.PatientFhirId))
        {
            throw new ArgumentException("PatientFhirId es obligatorio.", nameof(cmd));
        }
        if (string.IsNullOrWhiteSpace(cmd.CodeText))
        {
            throw new ArgumentException("CodeText es obligatorio.", nameof(cmd));
        }

        var req = new UpsertLabOrderRequest(
            PartitionName: $"org-{cmd.OrganizationId}",
            PatientFhirId: cmd.PatientFhirId,
            EncounterFhirId: cmd.EncounterFhirId,
            RequesterPractitionerId: cmd.RequesterPractitionerId,
            Status: cmd.Status,
            Intent: "order",
            LoincCode: cmd.LoincCode,
            CodeDisplay: cmd.CodeDisplay,
            CodeText: cmd.CodeText,
            Priority: cmd.Priority,
            OccurrenceDateTime: cmd.OccurrenceDateTime,
            ReasonText: cmd.ReasonText,
            Note: cmd.Note,
            Requisition: cmd.Requisition,
            LocalCode: cmd.LocalCode,
            LocalSystem: cmd.LocalSystem);

        string id;
        if (string.IsNullOrWhiteSpace(cmd.Id))
        {
            id = await hapi.CreateLabOrderAsync(req, ct);
        }
        else
        {
            id = cmd.Id;
            await hapi.UpdateLabOrderAsync(id, req, ct);
        }

        var availability = await LabAvailability.ClassifyAsync(
            catalog, cmd.OrganizationId, cmd.LoincCode ?? cmd.LocalCode, ct);

        return new LabOrderDto(
            id, cmd.PatientFhirId, cmd.EncounterFhirId, cmd.RequesterPractitionerId, cmd.Status, "order",
            cmd.LoincCode, cmd.LocalCode, cmd.CodeText, cmd.CodeDisplay, cmd.Priority, cmd.OccurrenceDateTime,
            cmd.ReasonText, cmd.Note, cmd.Requisition, DateTimeOffset.UtcNow, availability);
    }
}

/// <summary>
/// Asigna un número de orden correlativo legible (F1): <c>ORD-{yyyyMMdd}-{seq}</c>
/// por organización, secuencial diario. Se llama al iniciar una nueva orden en el
/// encuentro; el valor se usa como <c>requisition</c> de todos sus exámenes.
/// </summary>
public sealed class AllocateOrderNumberHandler(ILabOperationsRepository ops, TimeProvider timeProvider)
{
    public async Task<string> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var day = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var seq = await ops.NextOrderSeqAsync(orgId, day, ct);
        return $"ORD-{day:yyyyMMdd}-{seq:D4}";
    }
}

public sealed class GetLabOrderHandler(IHapiFhirClient hapi, ILabCatalogRepository catalog)
{
    public async Task<LabOrderDto?> HandleAsync(Guid orgId, string id, CancellationToken ct = default)
    {
        var snap = await hapi.GetLabOrderAsync(id, $"org-{orgId}", ct);
        if (snap is null)
        {
            return null;
        }
        var availability = await LabAvailability.ClassifyAsync(catalog, orgId, snap.LoincCode, ct);
        return LabOrderDto.FromSnapshot(snap) with { Availability = availability };
    }
}

public sealed class ListLabOrdersByEncounterHandler(IHapiFhirClient hapi, ILabCatalogRepository catalog)
{
    public async Task<IReadOnlyList<LabOrderDto>> HandleAsync(Guid orgId, string encounterId, CancellationToken ct = default)
    {
        var rows = await hapi.SearchLabOrdersByEncounterAsync(encounterId, $"org-{orgId}", ct);
        return await LabOrderListMapper.ClassifyAll(catalog, orgId, rows, ct);
    }
}

public sealed class ListLabOrdersByPatientHandler(IHapiFhirClient hapi, ILabCatalogRepository catalog)
{
    public async Task<IReadOnlyList<LabOrderDto>> HandleAsync(Guid orgId, string patientId, CancellationToken ct = default)
    {
        var rows = await hapi.SearchLabOrdersByPatientAsync(patientId, $"org-{orgId}", ct);
        return await LabOrderListMapper.ClassifyAll(catalog, orgId, rows, ct);
    }
}

internal static class LabOrderListMapper
{
    // Clasifica secuencialmente (DbContext no es thread-safe → no Task.WhenAll).
    public static async Task<IReadOnlyList<LabOrderDto>> ClassifyAll(
        ILabCatalogRepository catalog, Guid orgId, IReadOnlyList<LabOrderSnapshot> rows, CancellationToken ct)
    {
        var list = new List<LabOrderDto>(rows.Count);
        foreach (var s in rows)
        {
            var availability = await LabAvailability.ClassifyAsync(catalog, orgId, s.LoincCode, ct);
            list.Add(LabOrderDto.FromSnapshot(s) with { Availability = availability });
        }
        return list;
    }
}
