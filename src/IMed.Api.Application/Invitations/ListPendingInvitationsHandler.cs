using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Invitations;

namespace IMed.Api.Application.Invitations;

public sealed class ListPendingInvitationsHandler(IInvitationsRepository invitationsRepo)
{
    public async Task<IReadOnlyList<InvitationListItem>> HandleAsync(
        ListPendingInvitationsQuery query,
        CancellationToken cancellationToken = default)
    {
        var invitations = await invitationsRepo.ListPendingForOrganizationAsync(
            query.OrganizationId, cancellationToken);

        return invitations
            .Select(i => new InvitationListItem(
                Id: i.Id,
                Email: i.Email,
                Role: InvitationRoleNames.ToRealmRole(i.Role),
                SpecialtyHint: i.SpecialtyHint,
                CreatedAt: i.CreatedAt,
                KeycloakUserId: i.KeycloakUserId))
            .ToList();
    }
}
