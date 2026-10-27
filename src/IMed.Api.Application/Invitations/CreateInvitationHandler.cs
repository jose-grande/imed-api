using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.Outbox;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Invitations;

/// <summary>
/// Excepción lanzada cuando el caller intenta invitar a un email que ya tiene
/// un slot ocupado (pending invite o miembro activo o user en KC en otra org).
/// El endpoint mapea esto a 409 Conflict con detalle del motivo.
/// </summary>
public sealed class InvitationConflictException(string detail, string code) : Exception(detail)
{
    public string Code { get; } = code;
}

public sealed class CreateInvitationHandler(
    IInvitationsRepository invitationsRepo,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    IKeycloakAdminClient keycloakAdmin,
    IInvitationsPersistence persistence,
    ISpecialtyRepository specialtyRepo,
    IAppAuditLog auditLog,
    InvitationKeycloakOptions kcInviteOptions,
    ILogger<CreateInvitationHandler> logger,
    TimeProvider timeProvider,
    ILocationRepository locations,
    ILocationProvisioning locationProvisioning)
{
    public async Task<CreateInvitationResult> HandleAsync(
        CreateInvitationCommand command,
        CancellationToken cancellationToken = default)
    {
        var email = command.Email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new ArgumentException("Email es obligatorio.", nameof(command));
        }

        var org = await orgRepo.GetByIdAsync(command.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Organization {command.OrganizationId} no existe en imed_app.");

        // SpecialtyHint: si llegó, validar contra el catálogo. Evita que un
        // typeo en la UI persista un código inventado en la invitación.
        if (!string.IsNullOrWhiteSpace(command.SpecialtyHint))
        {
            var specialty = await specialtyRepo.GetByCodeAsync(command.SpecialtyHint, cancellationToken)
                ?? throw new ArgumentException(
                    $"SpecialtyHint '{command.SpecialtyHint}' no existe en el catálogo.",
                    nameof(command));
            _ = specialty;
        }

        // Las sedes se validan antes de crear nada en Keycloak: una sede ajena o inactiva no puede
        // descubrirse con el usuario ya creado (FAC-9).
        var sedesPedidas = (command.LocationIds ?? []).Distinct().ToList();
        if (sedesPedidas.Count > 0)
        {
            var activas = (await locations.ListAsync(command.OrganizationId, activeOnly: true, cancellationToken))
                .Select(l => l.Id).ToHashSet();
            if (sedesPedidas.Any(id => !activas.Contains(id)))
            {
                throw new ArgumentException("Alguna de las sedes no existe o está inactiva.", nameof(command));
            }
        }

        // 1. Idempotency: ¿ya hay invitación pending para este email en esta org?
        var existingPending = await invitationsRepo.GetPendingByEmailAndOrganizationAsync(
            email, command.OrganizationId, cancellationToken);
        if (existingPending is not null)
        {
            throw new InvitationConflictException(
                "Ya hay una invitación pendiente para este email en esta organización.",
                code: "invitation_pending_exists");
        }

        // 2. ¿El email ya tiene cuenta en KC?
        var existingKcUser = await keycloakAdmin.GetUserByEmailAsync(email, cancellationToken);
        Guid keycloakUserId;
        if (existingKcUser is not null)
        {
            // ¿Es miembro de esta org?
            var assignment = await assignmentRepo.GetAsync(
                existingKcUser.Id, command.OrganizationId, cancellationToken);
            if (assignment is not null)
            {
                throw new InvitationConflictException(
                    "Este email ya es miembro de la organización.",
                    code: "already_member");
            }

            // Tiene cuenta KC pero no es miembro acá — caso edge: usuario con KC
            // user creado en otra org. Pedimos que use join request en lugar de
            // sobrescribir su user existente con required actions de esta org.
            throw new InvitationConflictException(
                "Este email ya tiene cuenta en otra organización. " +
                "Pídale que envíe una solicitud de ingreso a esta organización.",
                code: "user_exists_other_org");
        }
        else
        {
            // 3. Crear KC user con required actions UPDATE_PASSWORD + VERIFY_EMAIL.
            //    El user no podrá loguear hasta completar las acciones.
            var realmRole = InvitationRoleNames.ToRealmRole(command.Role);
            var attrs = new Dictionary<string, string[]>
            {
                ["organization_id"] = [command.OrganizationId.ToString()],
                ["pending_role"] = [realmRole]
            };
            if (!string.IsNullOrWhiteSpace(command.SpecialtyHint))
            {
                attrs["pending_specialty"] = [command.SpecialtyHint];
            }

            keycloakUserId = await keycloakAdmin.CreateUserAsync(new CreateKeycloakUserRequest
            {
                Email = email,
                Username = email,
                Enabled = true,
                EmailVerified = false,
                RequiredActions = ["UPDATE_PASSWORD", "VERIFY_EMAIL"],
                Attributes = attrs
            }, cancellationToken);

            // 4. Assign realm role. Idempotente — KC ignora duplicados.
            await keycloakAdmin.AssignRealmRoleAsync(keycloakUserId, realmRole, cancellationToken);
        }

        // 5. Persistir invitation con KC user attached + audit log atómicamente.
        var invitation = Invitation.Create(
            email: email,
            organizationId: command.OrganizationId,
            organizationFhirId: org.FhirId,
            role: command.Role,
            specialtyHint: command.SpecialtyHint,
            createdBy: command.CreatedBy,
            timeProvider: timeProvider);
        invitation.AttachKeycloakUser(keycloakUserId);

        await persistence.CreatePendingAsync(invitation, command.CreatedBy, cancellationToken);

        // 5.5 Sedes (FAC-9). Se asignan ya, con el usuario de Keycloak recién creado: cuando acepte
        //     y entre por primera vez, su selector de sede ya sabe dónde trabaja.
        if (sedesPedidas.Count > 0)
        {
            await locations.ReplaceAssignmentsAsync(
                command.OrganizationId, keycloakUserId, sedesPedidas, sedesPedidas[0],
                command.CreatedBy, cancellationToken);
        }
        else
        {
            await locationProvisioning.EnsureUserAssignedAsync(
                command.OrganizationId, keycloakUserId, command.CreatedBy, cancellationToken);
        }

        // 6. Hot-path: disparar email via KC. Si falla, escribimos outbox event
        //    para retry idempotente (KC genera un token nuevo cada vez que se invoca).
        var emailDispatched = false;
        try
        {
            await keycloakAdmin.ExecuteActionsEmailAsync(
                keycloakUserId,
                actions: ["UPDATE_PASSWORD", "VERIFY_EMAIL"],
                lifespanSeconds: kcInviteOptions.LifespanSeconds,
                redirectUri: kcInviteOptions.RedirectUri,
                clientId: kcInviteOptions.ClientId,
                cancellationToken);
            emailDispatched = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "executeActionsEmail falló para invitation {InvitationId} (user {UserId}); outbox pendiente.",
                invitation.Id, keycloakUserId);

            var payload = JsonSerializer.Serialize(new InvitationEmailPendingEvent.Payload(
                InvitationId: invitation.Id,
                KeycloakUserId: keycloakUserId,
                Email: email,
                Actions: ["UPDATE_PASSWORD", "VERIFY_EMAIL"],
                LifespanSeconds: kcInviteOptions.LifespanSeconds,
                RedirectUri: kcInviteOptions.RedirectUri,
                ClientId: kcInviteOptions.ClientId));
            await persistence.RecordEmailPendingAsync(invitation.Id, payload, cancellationToken);
        }

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "invitations",
            action: emailDispatched ? "created" : "created_email_pending",
            severity: emailDispatched ? AuditSeverity.Info : AuditSeverity.Warning,
            actorUserId: command.CreatedBy,
            organizationId: command.OrganizationId,
            target: $"Invitation:{invitation.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                invitation.Email,
                Role = realmRoleName(command.Role),
                command.SpecialtyHint,
                KeycloakUserId = keycloakUserId
            }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "Invitation creada {InvitationId} email={Email} org={OrgId} role={Role} emailDispatched={EmailDispatched}",
            invitation.Id, email, command.OrganizationId, command.Role, emailDispatched);

        return new CreateInvitationResult(invitation.Id, keycloakUserId, emailDispatched);
    }

    private static string realmRoleName(InvitationRole role) => InvitationRoleNames.ToRealmRole(role);
}

/// <summary>
/// Configuración del flow de invitación que viaja con el handler — extracted
/// de <c>appsettings</c> en startup. El handler la usa al disparar
/// <c>executeActionsEmail</c>.
/// </summary>
public sealed record InvitationKeycloakOptions
{
    public const string SectionName = "Invitations:Keycloak";

    /// <summary>Lifespan del action token (segundos). Default: 7 días.</summary>
    public int LifespanSeconds { get; init; } = 7 * 24 * 3600;

    /// <summary>
    /// URL del frontend a la que KC redirige tras completar las required actions.
    /// Típicamente <c>https://app.imed.hn/onboarding/welcome</c>.
    /// </summary>
    public string? RedirectUri { get; init; }

    /// <summary>
    /// Client ID del SPA OIDC para que KC valide el redirect URI contra los
    /// registered redirects del client.
    /// </summary>
    public string? ClientId { get; init; }
}

/// <summary>
/// Puerto de persistencia de invitations. Las operaciones que mutan estado
/// (create pending, mark revoked, mark expired batch) se hacen en una sola
/// transacción local — incluyen el outbox event si aplica.
/// </summary>
public interface IInvitationsPersistence
{
    Task CreatePendingAsync(Invitation invitation, Guid createdBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotente: si ya hay un outbox event <c>invitation.email-pending</c>
    /// no-dispatched para esta invitación, no inserta otro.
    /// </summary>
    Task RecordEmailPendingAsync(Guid invitationId, string payloadJson, CancellationToken cancellationToken = default);

    Task UpdateAsync(Invitation invitation, CancellationToken cancellationToken = default);
}
