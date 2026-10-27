using IMed.Api.Domain.Invitations;

namespace IMed.Api.Application.JoinRequests;

/// <summary>
/// Command público para enviar una solicitud de ingreso. El caller no está
/// autenticado, así que no hay <c>CreatedBy</c>; la org se identifica por su
/// FHIR id (opaco al usuario).
/// </summary>
public sealed record SubmitJoinRequestCommand(
    string Email,
    string FullName,
    string OrganizationFhirId,
    string? Message);

public sealed record SubmitJoinRequestResult(
    Guid JoinRequestId,
    bool AdminNotified);

public sealed record ApproveJoinRequestCommand(
    Guid JoinRequestId,
    Guid OrganizationId,
    Guid ResolvedBy,
    InvitationRole Role = InvitationRole.Practitioner,
    IReadOnlyList<Guid>? LocationIds = null);

public sealed record ApproveJoinRequestResult(
    Guid JoinRequestId,
    Guid LinkedInvitationId,
    bool ApplicantNotified);

public sealed record RejectJoinRequestCommand(
    Guid JoinRequestId,
    Guid OrganizationId,
    Guid ResolvedBy,
    string? Reason);

public sealed record ListPendingJoinRequestsQuery(Guid OrganizationId);

public sealed record JoinRequestListItem(
    Guid Id,
    string Email,
    string FullName,
    string? Message,
    DateTimeOffset CreatedAt);

/// <summary>
/// Excepción de conflicto del flow de join requests. El endpoint la mapea a
/// 409/404 según el <see cref="Code"/>.
/// </summary>
public sealed class JoinRequestConflictException(string detail, string code) : Exception(detail)
{
    public string Code { get; } = code;
}
