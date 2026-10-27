using System.Text.Json;
using IMed.Api.Application.Locations;
using IMed.Api.Domain.Outbox;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Encola la sincronización FHIR de una sede o razón social en el mismo <c>SaveChanges</c> que la
/// modifica. Que viajen juntos es lo que garantiza que ningún cambio quede sin proyectar a HAPI.
/// </summary>
internal static class LocationFhirSyncOutbox
{
    public static void EnqueueLocation(AppDbContext db, Guid organizationId, Guid locationId) =>
        db.OutboxEvents.Add(OutboxEvent.Create(
            aggregate: $"Location:{locationId}",
            type: LocationFhirSyncEvent.OutboxEventType,
            payloadJson: JsonSerializer.Serialize(
                new LocationFhirSyncEvent.Payload(organizationId, locationId, null))));

    public static void EnqueueLegalEntity(AppDbContext db, Guid organizationId, Guid legalEntityId) =>
        db.OutboxEvents.Add(OutboxEvent.Create(
            aggregate: $"LegalEntity:{legalEntityId}",
            type: LocationFhirSyncEvent.OutboxEventType,
            payloadJson: JsonSerializer.Serialize(
                new LocationFhirSyncEvent.Payload(organizationId, null, legalEntityId))));
}

/// <summary>Encola la sincronización de sedes del profesional en el mismo <c>SaveChanges</c>.</summary>
internal static class PractitionerLocationsOutbox
{
    public static void Enqueue(AppDbContext db, Guid organizationId, Guid userId) =>
        db.OutboxEvents.Add(OutboxEvent.Create(
            aggregate: $"User:{userId}:Org:{organizationId}",
            type: PractitionerLocationsSyncEvent.OutboxEventType,
            payloadJson: JsonSerializer.Serialize(
                new PractitionerLocationsSyncEvent.Payload(organizationId, userId))));
}
