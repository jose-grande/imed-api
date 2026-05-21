using IMed.Api.Domain.Invitations;

namespace IMed.Api.Application.Invitations;

/// <summary>
/// Command para emitir una invitación. El handler resuelve la org del JWT
/// (via <c>OrganizationId</c>) y el caller hace el check de role en el endpoint.
/// </summary>
public sealed record CreateInvitationCommand(
    Guid OrganizationId,
    Guid CreatedBy,
    string Email,
    InvitationRole Role,
    string? SpecialtyHint);

public sealed record CreateInvitationResult(
    Guid InvitationId,
    Guid KeycloakUserId,
    bool EmailDispatched);

public sealed record RevokeInvitationCommand(
    Guid InvitationId,
    Guid OrganizationId,
    Guid RevokedBy);

public sealed record ListPendingInvitationsQuery(Guid OrganizationId);

public sealed record MarkInvitationsExpiredCommand(
    Guid OrganizationId,
    DateTimeOffset Threshold);

/// <summary>
/// DTO de invitación pendiente devuelto al frontend (Org Admin).
/// </summary>
public sealed record InvitationListItem(
    Guid Id,
    string Email,
    string Role,
    string? SpecialtyHint,
    DateTimeOffset CreatedAt,
    Guid? KeycloakUserId);
