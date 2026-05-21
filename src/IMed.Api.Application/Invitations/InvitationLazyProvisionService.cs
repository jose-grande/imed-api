using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Practitioners;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Catalogs;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.Outbox;
using IMed.Api.Domain.Practitioners;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Invitations;

/// <summary>
/// Resuelve el lazy provision al primer login del invitee:
/// <list type="number">
///   <item>Crea Practitioner + PractitionerRole en HAPI dentro de la partición
///   de la org (con specialty hint si la invitación traía una).</item>
///   <item>Persiste <c>UserPractitionerAssignment</c> + acepta la invitation +
///   outbox <c>me.practitioner-profile-updated</c> + audit log atómicamente.</item>
///   <item>Hot-path: actualiza atributo <c>practitioner_id</c> en KC. Si falla,
///   el outbox <c>me.practitioner-profile-updated</c> retira con backoff.</item>
/// </list>
/// Si HAPI falla en (1), persiste un outbox <c>invitation.lazy-provision-pending</c>
/// para retry idempotente y reporta <c>InProgress=true</c> al caller. El user
/// puede re-pegar el GET y eventualmente verá su perfil completo.
/// </summary>
public sealed class InvitationLazyProvisionService(
    IHapiFhirClient hapiFhir,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    IInvitationLazyProvisionPersistence persistence,
    IKeycloakAdminClient keycloakAdmin,
    ISpecialtyRepository specialtyRepo,
    ILogger<InvitationLazyProvisionService> logger,
    TimeProvider timeProvider)
{
    public async Task<LazyProvisionResult> CompleteAsync(
        Invitation invitation,
        Guid keycloakUserId,
        string? givenNameClaim,
        string? familyNameClaim,
        string? emailClaim,
        CancellationToken cancellationToken = default)
    {
        // Idempotencia defensiva: si ya hay assignment para (user, org), la
        // invitación se aceptó en una corrida anterior pero quedó pending por
        // alguna razón. Solo cerramos el ciclo en BD.
        var existingAssignment = await assignmentRepo.GetAsync(
            keycloakUserId, invitation.OrganizationId, cancellationToken);
        if (existingAssignment is not null)
        {
            logger.LogInformation(
                "Lazy provision: assignment ya existe para user {UserId} en org {OrgId}; " +
                "solo aceptamos invitation {InvitationId}.",
                keycloakUserId, invitation.OrganizationId, invitation.Id);

            invitation.Accept(keycloakUserId, timeProvider);
            await persistence.UpdateInvitationAsync(invitation, cancellationToken);
            return new LazyProvisionResult(
                Completed: true,
                PractitionerFhirId: existingAssignment.PractitionerFhirId,
                PractitionerRoleFhirId: existingAssignment.PractitionerRoleFhirId,
                SpecialtyCode: invitation.SpecialtyHint,
                SpecialtyDisplay: null);
        }

        var org = await orgRepo.GetByIdAsync(invitation.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Organization {invitation.OrganizationId} no existe.");

        var partitionName = $"org-{invitation.OrganizationId}";

        // Specialty hint: re-validar contra el catálogo. La invitación puede
        // haberse emitido cuando un código existía y haberse retirado; si el
        // catálogo no lo trae, ignoramos el hint en lugar de bloquear el lazy
        // provision (UX: el user puede setear su specialty manualmente después).
        SpecialtyCode? specialty = null;
        if (!string.IsNullOrWhiteSpace(invitation.SpecialtyHint))
        {
            specialty = await specialtyRepo.GetByCodeAsync(invitation.SpecialtyHint, cancellationToken);
            if (specialty is null)
            {
                logger.LogWarning(
                    "Lazy provision: SpecialtyHint '{Hint}' no encontrado en catálogo; " +
                    "Practitioner creado sin specialty. Invitation {InvitationId}.",
                    invitation.SpecialtyHint, invitation.Id);
            }
        }

        // Datos del Practitioner: usamos lo que viene del JWT como mejor
        // approximation. El user puede actualizarlo después en /profile/practitioner.
        // Family es required en HAPI Practitioner; si el JWT no lo trae,
        // fallback al email (parte antes del @).
        var family = !string.IsNullOrWhiteSpace(familyNameClaim)
            ? familyNameClaim
            : DeriveFamilyFromEmail(emailClaim ?? invitation.Email);
        var given = !string.IsNullOrWhiteSpace(givenNameClaim)
            ? new[] { givenNameClaim }
            : new[] { DeriveGivenFromEmail(emailClaim ?? invitation.Email) };

        string practitionerFhirId;
        string? practitionerRoleFhirId = null;
        try
        {
            practitionerFhirId = await hapiFhir.CreatePractitionerAsync(new UpsertPractitionerRequest(
                PartitionName: partitionName,
                Family: family,
                Given: given,
                Prefix: null,
                LicenseNumber: null,
                Email: emailClaim ?? invitation.Email,
                Phone: null,
                Gender: null), cancellationToken);

            // El PractitionerRole es el vínculo Practitioner↔Organization; sin él
            // el invitee queda huérfano en HAPI y /api/team no lo lista. Specialty
            // queda opcional dentro del role (el user puede setearla en /profile/practitioner).
            practitionerRoleFhirId = await hapiFhir.CreatePractitionerRoleAsync(
                new UpsertPractitionerRoleRequest(
                    PartitionName: partitionName,
                    PractitionerFhirId: practitionerFhirId,
                    OrganizationFhirId: org.FhirId,
                    SpecialtyCode: specialty?.Code,
                    SpecialtyDisplay: specialty?.DisplayEs,
                    SpecialtySystem: specialty?.System ?? "http://snomed.info/sct"), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Lazy provision: HAPI create falló para invitation {InvitationId} user {UserId}; " +
                "outbox pendiente.",
                invitation.Id, keycloakUserId);

            var pendingPayload = JsonSerializer.Serialize(new InvitationLazyProvisionPendingEvent.Payload(
                InvitationId: invitation.Id,
                KeycloakUserId: keycloakUserId,
                OrganizationId: invitation.OrganizationId,
                OrganizationFhirId: org.FhirId,
                SpecialtyHint: invitation.SpecialtyHint,
                Email: emailClaim,
                GivenName: givenNameClaim,
                FamilyName: familyNameClaim));
            await persistence.RecordLazyProvisionPendingAsync(invitation.Id, pendingPayload, cancellationToken);
            return new LazyProvisionResult(
                Completed: false,
                PractitionerFhirId: null,
                PractitionerRoleFhirId: null,
                SpecialtyCode: invitation.SpecialtyHint,
                SpecialtyDisplay: specialty?.DisplayEs);
        }

        // Persistir assignment + Accept invitation + outbox practitioner-profile-updated
        // + audit log en una transacción.
        var assignment = UserPractitionerAssignment.Create(
            keycloakUserId: keycloakUserId,
            organizationId: invitation.OrganizationId,
            practitionerFhirId: practitionerFhirId,
            practitionerRoleFhirId: practitionerRoleFhirId,
            timeProvider: timeProvider);

        var profilePayload = JsonSerializer.Serialize(new PractitionerProfileUpdatedEvent.Payload(
            KeycloakUserId: keycloakUserId,
            OrganizationId: invitation.OrganizationId,
            PractitionerFhirId: practitionerFhirId));

        var auditEntry = AppAuditLogEntry.Create(
            category: "invitations",
            action: "completed",
            severity: AuditSeverity.Info,
            actorUserId: keycloakUserId,
            organizationId: invitation.OrganizationId,
            target: $"Invitation:{invitation.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                invitation.Email,
                Role = InvitationRoleNames.ToRealmRole(invitation.Role),
                PractitionerFhirId = practitionerFhirId,
                PractitionerRoleFhirId = practitionerRoleFhirId
            }),
            timeProvider: timeProvider);

        invitation.Accept(keycloakUserId, timeProvider);

        var outboxId = await persistence.CompleteLazyProvisionAsync(
            invitation, assignment, profilePayload, auditEntry, cancellationToken);

        // Hot-path: actualizar practitioner_id en KC. Si falla, el outbox
        // me.practitioner-profile-updated cubre el retry — es exactamente la
        // misma operación que el upsert manual del perfil.
        try
        {
            await keycloakAdmin.SetUserAttributesAsync(
                keycloakUserId,
                new Dictionary<string, string[]>
                {
                    ["practitioner_id"] = [practitionerFhirId]
                },
                cancellationToken);
            await persistence.MarkOutboxDispatchedAsync(outboxId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Lazy provision: KC sync falló para user {UserId}; outbox pendiente.", keycloakUserId);
        }

        logger.LogInformation(
            "Lazy provision OK invitation={InvitationId} user={UserId} practitioner={PractitionerId} " +
            "specialty={Specialty}",
            invitation.Id, keycloakUserId, practitionerFhirId, specialty?.Code ?? "(none)");

        return new LazyProvisionResult(
            Completed: true,
            PractitionerFhirId: practitionerFhirId,
            PractitionerRoleFhirId: practitionerRoleFhirId,
            SpecialtyCode: specialty?.Code,
            SpecialtyDisplay: specialty?.DisplayEs);
    }

    private static string DeriveFamilyFromEmail(string email)
    {
        // Fallback razonable: parte del email antes del @, primera letra mayúscula.
        // El user puede corregirlo en /profile/practitioner. Mejor que un valor
        // sintético tipo "Sin Apellido" que persiste en HAPI.
        var local = email.Split('@')[0];
        return local.Length == 0 ? "Sin apellido" : char.ToUpperInvariant(local[0]) + local[1..];
    }

    private static string DeriveGivenFromEmail(string email)
    {
        var local = email.Split('@')[0];
        return local.Length == 0 ? "Sin nombre" : char.ToUpperInvariant(local[0]) + local[1..];
    }
}

public sealed record LazyProvisionResult(
    bool Completed,
    string? PractitionerFhirId,
    string? PractitionerRoleFhirId,
    string? SpecialtyCode,
    string? SpecialtyDisplay);

/// <summary>
/// Persistencia atómica del lazy provision: combina varios writes en una
/// transacción para evitar estados inconsistentes (assignment sin invitation
/// accepted, outbox sin audit log, etc).
/// </summary>
public interface IInvitationLazyProvisionPersistence
{
    Task UpdateInvitationAsync(Invitation invitation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atómico:
    /// <list type="bullet">
    ///   <item>Insert <see cref="UserPractitionerAssignment"/>.</item>
    ///   <item>Update <see cref="Invitation"/> (status=accepted).</item>
    ///   <item>Insert outbox event <c>me.practitioner-profile-updated</c>.</item>
    ///   <item>Insert <see cref="AppAuditLogEntry"/>.</item>
    /// </list>
    /// Devuelve el id del outbox event para que el caller pueda marcarlo
    /// dispatched al completar la sync KC.
    /// </summary>
    Task<Guid> CompleteLazyProvisionAsync(
        Invitation invitation,
        UserPractitionerAssignment assignment,
        string profileUpdatedPayloadJson,
        AppAuditLogEntry auditEntry,
        CancellationToken cancellationToken = default);

    Task RecordLazyProvisionPendingAsync(
        Guid invitationId,
        string payloadJson,
        CancellationToken cancellationToken = default);

    Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default);
}
