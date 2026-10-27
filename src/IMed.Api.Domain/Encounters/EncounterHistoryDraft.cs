namespace IMed.Api.Domain.Encounters;

/// <summary>
/// Cambios pendientes a los antecedentes del paciente durante una consulta (Épica P, P-D1/P-D3).
/// </summary>
/// <remarks>
/// <para><b>Qué es y qué no es.</b> Los antecedentes se escriben en FHIR al cerrar la consulta,
/// dentro de la transacción del cierre (P-D1). Hasta entonces, lo que el médico va editando es
/// borrador de trabajo y vive acá: un refresco, un token vencido o cambiar de equipo no pueden
/// perderlo. <b>No es expediente</b>: la verdad clínica es FHIR.</para>
///
/// <para><b>Por qué no recursos FHIR en <c>draft</c>.</b> Aparecerían en las búsquedas de
/// antecedentes de cualquier otra pantalla —la preclínica de otra cita, la ficha del paciente— como
/// si ya fueran ciertos.</para>
///
/// <para><b>Al cerrar no se borra (P-D3, pedido del dueño):</b> se marca con
/// <see cref="CommittedAt"/> y el id de la <c>Provenance</c> del cierre, para que nadie lo lea como
/// pendiente ni como vigente.</para>
///
/// <para>El contenido (<see cref="PayloadJson"/>) lo define el frontend y el backend no lo
/// interpreta: es la lista de cambios tal como la arma la pantalla. Lo que el backend garantiza es
/// que no se pisen dos pestañas (<see cref="Version"/>) y que un borrador cerrado no se reabra.</para>
/// </remarks>
public sealed class EncounterHistoryDraft
{
    /// <summary>Tope del borrador. Una consulta realista anda en pocos kilobytes.</summary>
    public const int MaxPayloadChars = 64 * 1024;

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string EncounterFhirId { get; private set; } = string.Empty;
    public string PatientFhirId { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";

    /// <summary>Se incrementa en cada guardado; quien guarda dice qué versión leyó.</summary>
    public int Version { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset? CommittedAt { get; private set; }
    public Guid? CommittedBy { get; private set; }

    /// <summary><c>Provenance</c> FHIR del cierre que escribió estos cambios.</summary>
    public string? ProvenanceFhirId { get; private set; }

    public bool IsCommitted => CommittedAt is not null;

    private EncounterHistoryDraft() { }

    public static EncounterHistoryDraft Create(
        Guid organizationId, string encounterFhirId, string patientFhirId, TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(encounterFhirId) || string.IsNullOrWhiteSpace(patientFhirId))
        {
            throw new ArgumentException("El encuentro y el paciente son obligatorios.");
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new EncounterHistoryDraft
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            EncounterFhirId = encounterFhirId.Trim(),
            PatientFhirId = patientFhirId.Trim(),
            PayloadJson = "{}",
            Version = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Reemplaza el borrador.</summary>
    /// <exception cref="InvalidOperationException">El borrador ya se cerró, o alguien guardó antes.</exception>
    public void Save(string payloadJson, int expectedVersion, Guid? updatedBy, TimeProvider? timeProvider = null)
    {
        if (IsCommitted)
        {
            throw new InvalidOperationException("La consulta ya se cerró: sus antecedentes no admiten más cambios.");
        }
        if (expectedVersion != Version)
        {
            throw new InvalidOperationException(
                "Los antecedentes se modificaron desde otra pestaña o equipo. Recargá la consulta.");
        }
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > MaxPayloadChars)
        {
            throw new ArgumentException("El borrador está vacío o supera el tamaño admitido.", nameof(payloadJson));
        }
        PayloadJson = payloadJson;
        Version++;
        UpdatedBy = updatedBy;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Marca el borrador como escrito en FHIR. Idempotente: el cierre puede reintentarse.</summary>
    public void Commit(string? provenanceFhirId, Guid? committedBy, TimeProvider? timeProvider = null)
    {
        if (IsCommitted)
        {
            return;
        }
        CommittedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        CommittedBy = committedBy;
        ProvenanceFhirId = string.IsNullOrWhiteSpace(provenanceFhirId) ? null : provenanceFhirId.Trim();
    }
}
