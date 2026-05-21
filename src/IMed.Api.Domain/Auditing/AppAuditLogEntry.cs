namespace IMed.Api.Domain.Auditing;

/// <summary>
/// Auditoría app-level (NO clínica). Cubre eventos que NO viven en HAPI FHIR
/// pero requieren trazabilidad: login, bootstrap, cambio de tier, invitación,
/// dispatch del outbox, etc.
///
/// Eventos clínicos (lectura/escritura de Patient/Encounter/etc) se auditan en
/// HAPI vía <c>AuditEvent</c> (Épica E del Hito 1, PLAN §2.5).
/// </summary>
public sealed class AppAuditLogEntry
{
    public Guid Id { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }

    /// <summary>Categoría amplia (auth, organization, subscription, outbox, etc).</summary>
    public string Category { get; private set; } = string.Empty;

    /// <summary>Acción específica (login, bootstrap, tier_changed, dispatched, etc).</summary>
    public string Action { get; private set; } = string.Empty;

    /// <summary>Severity: info, warning, error. Útil para filtros operativos.</summary>
    public AuditSeverity Severity { get; private set; }

    /// <summary>UUID del usuario en Keycloak (claim <c>sub</c>). Null si la acción es del sistema.</summary>
    public Guid? ActorUserId { get; private set; }

    /// <summary>Organization a la que aplica la acción. Null si es pre-bootstrap.</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Recurso afectado (ej: <c>Organization:abc</c>, <c>OutboxEvent:xyz</c>).</summary>
    public string? Target { get; private set; }

    /// <summary>Correlation-id propagado desde el frontend (header X-Correlation-Id).</summary>
    public string? CorrelationId { get; private set; }

    /// <summary>IP del cliente, si está disponible.</summary>
    public string? RemoteIp { get; private set; }

    /// <summary>Payload arbitrario JSON (jsonb). Detalles de la acción.</summary>
    public string Payload { get; private set; } = "{}";

    private AppAuditLogEntry() { }

    public static AppAuditLogEntry Create(
        string category,
        string action,
        AuditSeverity severity = AuditSeverity.Info,
        Guid? actorUserId = null,
        Guid? organizationId = null,
        string? target = null,
        string? correlationId = null,
        string? remoteIp = null,
        string payloadJson = "{}",
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("category es obligatorio.", nameof(category));
        }
        if (string.IsNullOrWhiteSpace(action))
        {
            throw new ArgumentException("action es obligatorio.", nameof(action));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new AppAuditLogEntry
        {
            Id = Guid.CreateVersion7(now),
            OccurredAt = now,
            Category = category,
            Action = action,
            Severity = severity,
            ActorUserId = actorUserId,
            OrganizationId = organizationId,
            Target = target,
            CorrelationId = correlationId,
            RemoteIp = remoteIp,
            Payload = payloadJson
        };
    }
}

public enum AuditSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2,
}
