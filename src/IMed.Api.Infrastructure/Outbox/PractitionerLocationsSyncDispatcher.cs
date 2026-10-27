using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Locations;
using IMed.Api.Application.Outbox;
using IMed.Api.Domain.Locations;
using IMed.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha <c>practitioner-role.locations-sync</c>: escribe en el <c>PractitionerRole</c> del
/// profesional las sedes donde atiende, la principal primero (FAC-9).
///
/// <para>Antes de escribir el rol proyecta las sedes que todavía no están en HAPI: el rol las
/// referencia y HAPI valida la integridad referencial al escribir. Si el profesional todavía no
/// tiene rol —la invitación no se completó— no hay nada que hacer: el alta del rol vuelve a
/// encolar este evento.</para>
/// </summary>
public sealed class PractitionerLocationsSyncDispatcher(
    AppDbContext db,
    IHapiFhirClient hapi,
    LocationFhirProjector projector,
    ILogger<PractitionerLocationsSyncDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => PractitionerLocationsSyncEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        PractitionerLocationsSyncEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<PractitionerLocationsSyncEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' es null.");
        }

        var asignacion = await db.UserPractitionerAssignments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.KeycloakUserId == payload.UserId
                                   && x.OrganizationId == payload.OrganizationId, cancellationToken);
        if (asignacion?.PractitionerRoleFhirId is not { } roleId)
        {
            return;
        }

        var sedes = await db.UserLocations.AsNoTracking()
            .Where(a => a.OrganizationId == payload.OrganizationId && a.UserId == payload.UserId)
            .Join(db.OrganizationLocations.AsNoTracking().Where(l => l.Active),
                a => a.LocationId, l => l.Id, (a, l) => new { a.IsPrimary, l.Id, l.LocationFhirId })
            .OrderByDescending(x => x.IsPrimary)
            .ToListAsync(cancellationToken);

        foreach (var sede in sedes.Where(s => s.LocationFhirId is null))
        {
            await projector.ProjectLocationAsync(payload.OrganizationId, sede.Id, cancellationToken);
        }

        var partition = $"org-{payload.OrganizationId}";
        var rol = await hapi.GetPractitionerRoleAsync(roleId, partition, cancellationToken);
        if (rol is null || rol.PractitionerFhirId is null || rol.OrganizationFhirId is null)
        {
            logger.LogWarning(
                "Sedes del profesional no sincronizadas: el PractitionerRole {RoleId} no existe o está incompleto.",
                roleId);
            return;
        }

        await hapi.UpdatePractitionerRoleAsync(roleId, new UpsertPractitionerRoleRequest(
            PartitionName: partition,
            PractitionerFhirId: rol.PractitionerFhirId,
            OrganizationFhirId: rol.OrganizationFhirId,
            SpecialtyCode: rol.SpecialtyCode,
            SpecialtyDisplay: rol.SpecialtyDisplay,
            SpecialtySystem: rol.SpecialtySystem ?? "http://snomed.info/sct",
            Active: rol.Active,
            // Lista vacía y no null: un profesional al que le quitaron todas las sedes (no debería
            // pasar, la invariante lo impide) tiene que quedar sin sedes en FHIR, no con las viejas.
            LocationFhirIds: sedes.Select(s => OrganizationLocation.FhirIdFor(s.Id)).ToList()), cancellationToken);
    }
}
