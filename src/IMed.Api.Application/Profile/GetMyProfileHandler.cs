using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Profile;

/// <summary>
/// Handler de <see cref="GetMyProfileQuery"/>. Lee la organization del repositorio
/// si el usuario tiene una asignada. Si <c>OrganizationId</c> es null (usuario
/// recién registrado, antes del bootstrap), devuelve <c>Organization = null</c>.
/// </summary>
public sealed class GetMyProfileHandler(IOrganizationProfileRepository orgRepo)
{
    public async Task<MyProfileResponse> HandleAsync(
        GetMyProfileQuery query,
        UserProfile user,
        CancellationToken cancellationToken = default)
    {
        OrganizationProfile? org = null;
        if (query.OrganizationId.HasValue)
        {
            org = await orgRepo.GetByIdAsync(query.OrganizationId.Value, cancellationToken);
        }

        return new MyProfileResponse(user, org);
    }
}

public interface IOrganizationProfileRepository
{
    Task<OrganizationProfile?> GetByIdAsync(Guid organizationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup público por FHIR id (no expone el Guid interno). Usado por el
    /// endpoint anónimo de join requests: el solicitante pega un link tipo
    /// <c>/join?org=&lt;fhirId&gt;</c>.
    /// </summary>
    Task<OrganizationProfile?> GetByFhirIdAsync(string fhirId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Devuelve el KC user id del founder de la org (primer
    /// <c>user_tier_assignment</c> ordenado por <c>AssignedAt</c>). Null si no
    /// hay registro — caso patológico que no debería ocurrir post-bootstrap.
    /// </summary>
    Task<Guid?> GetFounderUserIdAsync(Guid organizationId, CancellationToken cancellationToken = default);
}
