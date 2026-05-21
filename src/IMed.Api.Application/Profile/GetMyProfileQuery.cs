namespace IMed.Api.Application.Profile;

/// <summary>
/// Query del endpoint <c>GET /api/me</c>. Devuelve datos del perfil del usuario
/// que NO viajan en el JWT — esencialmente, el nombre legible de la organization
/// (que vive en imed_app/HAPI). El tier ya viene en el JWT, pero lo replicamos
/// acá para que la UI no tenga que decodificar el token.
/// </summary>
public sealed record GetMyProfileQuery(Guid KeycloakUserId, Guid? OrganizationId);

public sealed record MyProfileResponse(
    UserProfile User,
    OrganizationProfile? Organization);

public sealed record UserProfile(
    Guid KeycloakUserId,
    string? Name,
    string? Email,
    IReadOnlyList<string> Roles);

public sealed record OrganizationProfile(
    Guid Id,
    string Name,
    string FhirId,
    string ContractStatus,
    DateTimeOffset? TrialEndsAt);
