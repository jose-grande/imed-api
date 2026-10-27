using System.Text.Json;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Outbox;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha <c>location.fhir-sync</c>: proyecta a HAPI la sede o la razón social que cambió
/// (FAC-9). La escritura la hace <see cref="LocationFhirProjector"/>, que también usa la
/// sincronización de sedes del profesional.
/// </summary>
public sealed class LocationFhirSyncDispatcher(LocationFhirProjector projector) : IOutboxDispatcher
{
    public string EventType => LocationFhirSyncEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        LocationFhirSyncEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LocationFhirSyncEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' es null.");
        }

        if (payload.LocationId is { } locationId)
        {
            await projector.ProjectLocationAsync(payload.OrganizationId, locationId, cancellationToken);
        }
        else if (payload.LegalEntityId is { } legalEntityId)
        {
            await projector.ProjectLegalEntityAsync(payload.OrganizationId, legalEntityId, cancellationToken);
        }
    }
}
