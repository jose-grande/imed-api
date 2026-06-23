using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Devuelve los datos de la etiqueta de una muestra (R1-F3, LIS-RD-18): el
/// accession es el valor del código de barras (Code128, renderizado en el
/// frontend) + nombre/identificador del paciente, tipo de muestra y fecha.
/// </summary>
public sealed class GetSpecimenLabelHandler(IHapiFhirClient hapi)
{
    public async Task<LabelDto?> HandleAsync(Guid orgId, string specimenFhirId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var spec = await hapi.GetSpecimenAsync(specimenFhirId, partition, ct);
        if (spec is null || string.IsNullOrWhiteSpace(spec.AccessionNumber))
        {
            return null;
        }

        var patient = string.IsNullOrWhiteSpace(spec.PatientFhirId)
            ? null
            : await hapi.GetPatientAsync(spec.PatientFhirId, partition, ct);

        return new LabelDto(
            Accession: spec.AccessionNumber!,
            BarcodeValue: spec.AccessionNumber!,
            PatientName: patient?.DisplayName,
            PatientFhirId: spec.PatientFhirId,
            SpecimenType: spec.TypeText,
            ReceivedTime: spec.ReceivedTime,
            IdentifierLabel: patient?.IdentifierLabel,
            IdentifierValue: patient?.IdentifierValue);
    }
}

/// <summary>
/// Cola de órdenes pendientes (R1-F5, LIS-RD-10): órdenes de laboratorio activas
/// de la organización que aún no fueron recibidas (sin `order_intake`). Reemplaza
/// el tecleo manual del ID de orden en recepción.
/// </summary>
public sealed class GetPendingOrdersHandler(IHapiFhirClient hapi, ILabOperationsRepository ops)
{
    public async Task<IReadOnlyList<PendingOrder>> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = await hapi.SearchActiveLabOrdersAsync(partition, ct);

        var pending = new List<PendingOrder>();
        foreach (var o in orders)
        {
            // Ya tiene intake (recibida/rechazada/bloqueada/referida) → no está pendiente.
            var intake = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
            if (intake is not null)
            {
                continue;
            }
            pending.Add(new PendingOrder(
                o.Id, o.PatientFhirId, o.EncounterFhirId, o.LoincCode, o.CodeText, o.Priority, o.AuthoredOn));
        }
        return pending;
    }
}

/// <summary>
/// Cola de toma de muestra on-site (R1-F7, LIS-RD-24): órdenes activas sin recibir
/// cuyo tipo de muestra se **toma en el laboratorio** (sangre, hisopado, OGTT).
/// Distinta de la recepción de muestras que el paciente trae.
/// </summary>
public sealed class GetCollectionQueueHandler(
    IHapiFhirClient hapi, ILabCatalogRepository catalog, ILabOperationsRepository ops)
{
    public async Task<IReadOnlyList<CollectionQueueItem>> HandleAsync(Guid orgId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);
        var orders = await hapi.SearchActiveLabOrdersAsync(partition, ct);
        var specimenTypes = (await catalog.ListSpecimenTypesAsync(orgId, ct)).ToDictionary(s => s.Id);

        var items = new List<CollectionQueueItem>();
        foreach (var o in orders)
        {
            var intake = await ops.GetIntakeByServiceRequestAsync(orgId, o.Id, ct);
            if (intake is not null)
            {
                continue;
            }
            var (_, test) = await LabRouting.ResolveAsync(catalog, orgId, o.LoincCode, ct);
            SpecimenType? st = test?.SpecimenTypeId is Guid sid && specimenTypes.TryGetValue(sid, out var s) ? s : null;
            var mode = st?.CollectionMode ?? CollectionMode.OnSite;
            if (mode != CollectionMode.OnSite)
            {
                continue; // las que el paciente trae van a recepción, no a la cola de toma
            }
            items.Add(new CollectionQueueItem(
                o.Id, o.PatientFhirId, o.EncounterFhirId, o.LoincCode, o.CodeText,
                st?.Code ?? "UNKNOWN", st?.Name ?? "Sin clasificar",
                st?.DefaultMethod, st?.DefaultBodySite, test?.FastingHours, o.Priority));
        }
        return items;
    }
}
