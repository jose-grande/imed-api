namespace IMed.Api.Domain.JoinRequests;

/// <summary>
/// Solicitud anónima para incorporarse a una organización existente. El
/// solicitante (alguien que aún NO tiene cuenta en KC) llena un formulario
/// público; un Org Admin la revisa y la aprueba/rechaza.
///
/// <para>Aprobación dispara el mismo flow que <c>CreateInvitation</c> (Épica F3):
/// crea KC user + executeActionsEmail. La join request linkea a su invitation
/// resultante via <see cref="LinkedInvitationId"/>.</para>
///
/// <para>Estados terminales (<c>Approved</c>/<c>Rejected</c>) son inmutables.</para>
/// </summary>
public sealed class JoinRequest
{
    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string FullName { get; private set; } = string.Empty;
    public Guid OrganizationId { get; private set; }
    public string OrganizationFhirId { get; private set; } = string.Empty;

    /// <summary>Mensaje libre del solicitante para el admin (motivo, contexto, etc).</summary>
    public string? Message { get; private set; }

    public JoinRequestStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    /// <summary>KC user id del Org Admin que aprobó/rechazó. Null si pending.</summary>
    public Guid? ResolvedBy { get; private set; }

    /// <summary>Razón del rechazo. Null si Approved/Pending.</summary>
    public string? RejectionReason { get; private set; }

    /// <summary>
    /// Id de la <c>Invitation</c> creada al aprobar. Permite trazar la cadena
    /// JoinRequest → Invitation → Practitioner. Null si Pending/Rejected.
    /// </summary>
    public Guid? LinkedInvitationId { get; private set; }

    private JoinRequest() { }

    public static JoinRequest Create(
        string email,
        string fullName,
        Guid organizationId,
        string organizationFhirId,
        string? message,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email es obligatorio.", nameof(email));
        }
        if (string.IsNullOrWhiteSpace(fullName))
        {
            throw new ArgumentException("FullName es obligatorio.", nameof(fullName));
        }
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }
        if (string.IsNullOrWhiteSpace(organizationFhirId))
        {
            throw new ArgumentException("OrganizationFhirId es obligatorio.", nameof(organizationFhirId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new JoinRequest
        {
            Id = Guid.CreateVersion7(now),
            Email = email.Trim().ToLowerInvariant(),
            FullName = fullName.Trim(),
            OrganizationId = organizationId,
            OrganizationFhirId = organizationFhirId,
            Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim(),
            Status = JoinRequestStatus.Pending,
            CreatedAt = now
        };
    }

    public void Approve(Guid linkedInvitationId, Guid resolvedBy, TimeProvider? timeProvider = null)
    {
        if (Status != JoinRequestStatus.Pending)
        {
            throw new InvalidOperationException(
                $"No se puede aprobar JoinRequest {Id} en estado {Status}.");
        }
        if (linkedInvitationId == Guid.Empty)
        {
            throw new ArgumentException("LinkedInvitationId es obligatorio.", nameof(linkedInvitationId));
        }
        if (resolvedBy == Guid.Empty)
        {
            throw new ArgumentException("ResolvedBy es obligatorio.", nameof(resolvedBy));
        }
        Status = JoinRequestStatus.Approved;
        ResolvedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ResolvedBy = resolvedBy;
        LinkedInvitationId = linkedInvitationId;
    }

    public void Reject(string? reason, Guid resolvedBy, TimeProvider? timeProvider = null)
    {
        if (Status != JoinRequestStatus.Pending)
        {
            throw new InvalidOperationException(
                $"No se puede rechazar JoinRequest {Id} en estado {Status}.");
        }
        if (resolvedBy == Guid.Empty)
        {
            throw new ArgumentException("ResolvedBy es obligatorio.", nameof(resolvedBy));
        }
        Status = JoinRequestStatus.Rejected;
        ResolvedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        ResolvedBy = resolvedBy;
        RejectionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
    }
}

public enum JoinRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}
