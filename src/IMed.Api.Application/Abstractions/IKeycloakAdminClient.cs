namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto hacia Keycloak Admin API. La implementación vive en Infrastructure
/// y usa client_credentials flow para autenticarse como el service account
/// `imed-api-sa`.
/// </summary>
public interface IKeycloakAdminClient
{
    /// <summary>
    /// Setea/actualiza atributos custom del usuario. Merge con los existentes
    /// (no reemplazo total). Valores con array vacío se interpretan como borrar.
    /// </summary>
    Task SetUserAttributesAsync(
        Guid userId,
        IReadOnlyDictionary<string, string[]> attributes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asigna un rol realm-level a un usuario por nombre del rol.
    /// </summary>
    Task AssignRealmRoleAsync(
        Guid userId,
        string roleName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Crea un usuario en Keycloak. Idempotencia por email es responsabilidad
    /// del caller (chequear con <see cref="GetUserByEmailAsync"/> antes).
    /// Devuelve el id del user creado.
    /// </summary>
    Task<Guid> CreateUserAsync(
        CreateKeycloakUserRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Busca usuario por email exacto. Devuelve null si no existe. Si KC tiene
    /// más de un user con el mismo email (caso patológico), devuelve el primero.
    /// </summary>
    Task<KeycloakUserSnapshot?> GetUserByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Dispara el envío de email con action tokens via
    /// <c>PUT /admin/realms/{realm}/users/{id}/execute-actions-email</c>. KC
    /// genera un token firmado, lo embebe en el link del email, y manda el
    /// email usando su config <c>smtpServer</c>.
    ///
    /// <para><b>Crítico para el flow de invitations</b> (Épica F): el invitee
    /// recibe el email aquí, completa registration en páginas KC themed.</para>
    /// </summary>
    /// <param name="actions">Required actions a ejecutar. Para invite típico:
    /// <c>["UPDATE_PASSWORD", "VERIFY_EMAIL"]</c>.</param>
    /// <param name="lifespanSeconds">Validez del token. 7 días = 604800.</param>
    /// <param name="redirectUri">URL a la que el browser redirige tras completar
    /// las acciones. Típicamente la home del frontend imed-app.</param>
    /// <param name="clientId">Client ID del SPA OIDC. Permite que KC valide
    /// la URL de redirect contra los registered redirect URIs del client.</param>
    Task ExecuteActionsEmailAsync(
        Guid userId,
        IReadOnlyList<string> actions,
        int lifespanSeconds,
        string? redirectUri,
        string? clientId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista los roles realm-level asignados al usuario. Usado por
    /// <c>GET /api/team</c> para incluir el role en <c>TeamMember</c>.
    /// </summary>
    Task<IReadOnlyList<string>> GetUserRealmRolesAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Habilita o deshabilita el user en Keycloak. Mutación de campo core (no
    /// atributo custom) — KC bloquea el login de users con <c>enabled=false</c>.
    /// Usado por el revoke de invitations para defensa adicional contra users
    /// que completen el flow después de la revocación.
    /// </summary>
    Task SetUserEnabledAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup por id KC. Devuelve null si el user no existe (404 → null).
    /// Usado por F4 para obtener el email del founder al notificar de una nueva
    /// join request.
    /// </summary>
    Task<KeycloakUserSnapshot?> GetUserByIdAsync(
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remueve un realm role asignado al user. Idempotente: si el role no
    /// estaba asignado, no-op.
    /// </summary>
    Task RemoveRealmRoleAsync(
        Guid userId,
        string roleName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lista los KC user ids que tienen el realm role asignado. Usado por F5
    /// para contar org_admins de una organización (filtrando luego por
    /// <c>organization_id</c> attribute) — necesario para detectar el escenario
    /// "único admin intenta degradarse" y bloquearlo (lock-out).
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersWithRealmRoleAsync(
        string roleName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request para crear un user en KC. <see cref="RequiredActions"/> dispara
/// el flow standard — si la lista incluye <c>UPDATE_PASSWORD</c>, el user
/// no podrá loguear hasta completar la acción via <c>ExecuteActionsEmailAsync</c>.
/// </summary>
public sealed record CreateKeycloakUserRequest
{
    public required string Email { get; init; }
    public required string Username { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public bool Enabled { get; init; } = true;
    public bool EmailVerified { get; init; }
    public IReadOnlyList<string> RequiredActions { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string[]> Attributes { get; init; } =
        new Dictionary<string, string[]>();
}

public sealed record KeycloakUserSnapshot
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public string? Username { get; init; }
    public bool Enabled { get; init; }
    public bool EmailVerified { get; init; }
}
