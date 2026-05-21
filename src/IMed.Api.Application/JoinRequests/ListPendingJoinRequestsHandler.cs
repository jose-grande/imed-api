using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.JoinRequests;

public sealed class ListPendingJoinRequestsHandler(IJoinRequestsRepository joinRequestsRepo)
{
    public async Task<IReadOnlyList<JoinRequestListItem>> HandleAsync(
        ListPendingJoinRequestsQuery query,
        CancellationToken cancellationToken = default)
    {
        var requests = await joinRequestsRepo.ListPendingForOrganizationAsync(
            query.OrganizationId, cancellationToken);

        return requests
            .Select(r => new JoinRequestListItem(
                Id: r.Id,
                Email: r.Email,
                FullName: r.FullName,
                Message: r.Message,
                CreatedAt: r.CreatedAt))
            .ToList();
    }
}
