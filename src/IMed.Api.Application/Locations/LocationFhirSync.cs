namespace IMed.Api.Application.Locations;

/// <summary>
/// Evento de outbox que lleva una sede o una razón social a HAPI (FAC-9).
///
/// <para><b>Por qué por outbox y no en línea.</b> Dar de alta una sede no puede fallar porque HAPI
/// esté caído: la sede existe en <c>imed_app</c> desde el primer momento y lo operativo ya cuelga
/// de ella. El recurso FHIR es la proyección para el lado clínico —<c>Encounter.location</c>,
/// <c>PractitionerRole.location</c>— y llega cuando HAPI responde.</para>
///
/// <para><b>El payload no lleva datos, solo ids.</b> El dispatcher relee el estado actual y lo
/// escribe con un <c>PUT</c> sobre un id determinístico. Así, dos eventos pendientes de la misma
/// sede, o uno reintentado, terminan en el mismo recurso con los datos más recientes.</para>
/// </summary>
public static class LocationFhirSyncEvent
{
    public const string OutboxEventType = "location.fhir-sync";

    /// <param name="LocationId">Sede a sincronizar (arrastra su razón social).</param>
    /// <param name="LegalEntityId">Razón social a sincronizar sola, cuando cambió y no una sede.</param>
    public sealed record Payload(Guid OrganizationId, Guid? LocationId, Guid? LegalEntityId);
}

/// <summary>
/// Evento de outbox que lleva las sedes de un profesional a su <c>PractitionerRole.location</c>
/// (FAC-9, FAC9-DEC-02). Se encola cada vez que cambian sus sedes o se crea su rol; el dispatcher
/// relee las asignaciones vigentes, así que el orden y los duplicados no importan.
/// </summary>
public static class PractitionerLocationsSyncEvent
{
    public const string OutboxEventType = "practitioner-role.locations-sync";

    public sealed record Payload(Guid OrganizationId, Guid UserId);
}
