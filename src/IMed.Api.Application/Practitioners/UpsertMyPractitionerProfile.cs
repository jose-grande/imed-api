using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Profile;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Practitioners;

/// <summary>
/// Command para crear/actualizar el perfil Practitioner del user logueado en
/// su org actual. Modela el caso de uso "Mi perfil profesional" (PRD §RF-25).
/// </summary>
public sealed record UpsertMyPractitionerProfileCommand(
    Guid KeycloakUserId,
    Guid OrganizationId,
    string Family,
    IReadOnlyList<string> Given,
    string? Prefix,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Gender,
    string? SpecialtyCode,
    string? SpecialtyDisplay);

public sealed record UpsertMyPractitionerProfileResult(
    string PractitionerFhirId,
    string? PractitionerRoleFhirId,
    bool PendingKeycloakSync);

/// <summary>
/// Caso de uso end-to-end con outbox como red de seguridad (mismo patrón que
/// <c>BootstrapOrganizationHandler</c>):
/// <list type="number">
///   <item>Resuelve org FHIR id + partition name.</item>
///   <item>Crea o reemplaza Practitioner + PractitionerRole en HAPI dentro de
///         la partición de la org.</item>
///   <item>Persiste <c>user_practitioner_assignments</c> + outbox event +
///         audit log en una sola transacción local.</item>
///   <item>Hot-path: setea atributo <c>practitioner_id</c> en Keycloak. Si OK
///         marca outbox dispatched; si falla, deja outbox pendiente para que
///         el worker reintente con backoff.</item>
/// </list>
/// </summary>
public sealed class UpsertMyPractitionerProfileHandler(
    IHapiFhirClient hapiFhir,
    IOrganizationProfileRepository orgRepo,
    IUserPractitionerAssignmentRepository assignmentRepo,
    IUpsertPractitionerProfilePersistence persistence,
    IKeycloakAdminClient keycloakAdmin,
    ISpecialtyRepository specialtyRepo,
    ILogger<UpsertMyPractitionerProfileHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<UpsertMyPractitionerProfileResult> HandleAsync(
        UpsertMyPractitionerProfileCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);

        var org = await orgRepo.GetByIdAsync(command.OrganizationId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Organization {command.OrganizationId} no existe en imed_app. " +
                "Bootstrap pendiente o token corrupto.");

        var partitionName = $"org-{command.OrganizationId}";

        // Si hay specialty code, lo validamos contra el catálogo. Evita guardar
        // un PractitionerRole con un code inventado.
        string? resolvedSpecialtyDisplay = command.SpecialtyDisplay;
        string specialtySystem = "http://snomed.info/sct";
        if (!string.IsNullOrWhiteSpace(command.SpecialtyCode))
        {
            var specialty = await specialtyRepo.GetByCodeAsync(command.SpecialtyCode, cancellationToken)
                ?? throw new ArgumentException(
                    $"SpecialtyCode '{command.SpecialtyCode}' no existe en el catálogo.",
                    nameof(command));
            resolvedSpecialtyDisplay = specialty.DisplayEs;
            specialtySystem = specialty.System;
        }

        var existing = await assignmentRepo.GetAsync(command.KeycloakUserId, command.OrganizationId, cancellationToken);
        var isCreate = existing is null;

        var upsertReq = new UpsertPractitionerRequest(
            PartitionName: partitionName,
            Family: command.Family,
            Given: command.Given,
            Prefix: command.Prefix,
            LicenseNumber: command.LicenseNumber,
            Email: command.Email,
            Phone: command.Phone,
            Gender: command.Gender);

        string practitionerFhirId;
        if (isCreate)
        {
            practitionerFhirId = await hapiFhir.CreatePractitionerAsync(upsertReq, cancellationToken);
        }
        else
        {
            practitionerFhirId = existing!.PractitionerFhirId;
            await hapiFhir.UpdatePractitionerAsync(practitionerFhirId, upsertReq, cancellationToken);
        }

        // PractitionerRole: solo se crea/reemplaza si la specialty está presente.
        // Sin specialty no tiene mucho sentido (FHIR exige al menos referencia a
        // organization, pero la UX es "elegí especialidad"). Mantenemos null
        // assignment.PractitionerRoleFhirId si no hay specialty.
        string? practitionerRoleFhirId = existing?.PractitionerRoleFhirId;
        if (!string.IsNullOrWhiteSpace(command.SpecialtyCode))
        {
            var roleReq = new UpsertPractitionerRoleRequest(
                PartitionName: partitionName,
                PractitionerFhirId: practitionerFhirId,
                OrganizationFhirId: org.FhirId,
                SpecialtyCode: command.SpecialtyCode,
                SpecialtyDisplay: resolvedSpecialtyDisplay,
                SpecialtySystem: specialtySystem);

            if (string.IsNullOrEmpty(practitionerRoleFhirId))
            {
                practitionerRoleFhirId = await hapiFhir.CreatePractitionerRoleAsync(roleReq, cancellationToken);
            }
            else
            {
                await hapiFhir.UpdatePractitionerRoleAsync(practitionerRoleFhirId, roleReq, cancellationToken);
            }
        }

        var now = timeProvider.GetUtcNow();
        var outboxEventId = await persistence.UpsertAsync(new UpsertPractitionerProfilePersistenceInput(
            KeycloakUserId: command.KeycloakUserId,
            OrganizationId: command.OrganizationId,
            PractitionerFhirId: practitionerFhirId,
            PractitionerRoleFhirId: practitionerRoleFhirId,
            IsCreate: isCreate,
            Now: now),
            cancellationToken);

        // Hot-path: actualizar atributo practitioner_id en KC para que el
        // próximo refresh del JWT lo incluya. Si falla, el outbox worker
        // reintenta con backoff.
        try
        {
            await keycloakAdmin.SetUserAttributesAsync(
                command.KeycloakUserId,
                new Dictionary<string, string[]>
                {
                    ["practitioner_id"] = [practitionerFhirId]
                },
                cancellationToken);

            await persistence.MarkOutboxDispatchedAsync(outboxEventId, cancellationToken);

            logger.LogInformation(
                "Practitioner profile sync OK. user={UserId} practitioner={PractitionerId} create={IsCreate}",
                command.KeycloakUserId, practitionerFhirId, isCreate);

            return new UpsertMyPractitionerProfileResult(
                practitionerFhirId, practitionerRoleFhirId, PendingKeycloakSync: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Practitioner profile guardado pero KC sync falló; outbox pendiente. user={UserId}",
                command.KeycloakUserId);
            return new UpsertMyPractitionerProfileResult(
                practitionerFhirId, practitionerRoleFhirId, PendingKeycloakSync: true);
        }
    }

    private static void Validate(UpsertMyPractitionerProfileCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Family))
        {
            throw new ArgumentException("Family (apellidos) es obligatorio.", nameof(command));
        }
        if (command.Given is null || command.Given.Count == 0 || command.Given.All(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Given (nombres) es obligatorio.", nameof(command));
        }
        // Gender FHIR R5 admite: male | female | other | unknown. Si llega algo distinto, lo limpiamos.
        if (!string.IsNullOrWhiteSpace(command.Gender)
            && command.Gender is not ("male" or "female" or "other" or "unknown"))
        {
            throw new ArgumentException(
                "Gender debe ser uno de: male, female, other, unknown.", nameof(command));
        }
    }
}

public interface IUpsertPractitionerProfilePersistence
{
    /// <summary>
    /// Persiste el assignment (insert si IsCreate, update si no) + outbox event
    /// <c>me.practitioner-profile-updated</c> + audit log dentro de una
    /// transacción. Devuelve el outbox event id para que el caller pueda
    /// marcarlo dispatched al completar la sincronización con Keycloak.
    /// </summary>
    Task<Guid> UpsertAsync(UpsertPractitionerProfilePersistenceInput input, CancellationToken cancellationToken = default);

    Task MarkOutboxDispatchedAsync(Guid outboxEventId, CancellationToken cancellationToken = default);
}

public sealed record UpsertPractitionerProfilePersistenceInput(
    Guid KeycloakUserId,
    Guid OrganizationId,
    string PractitionerFhirId,
    string? PractitionerRoleFhirId,
    bool IsCreate,
    DateTimeOffset Now);

/// <summary>
/// Tipo del outbox event — referenciado por el dispatcher en Infrastructure y
/// por la persistencia que lo emite.
/// </summary>
public static class PractitionerProfileUpdatedEvent
{
    public const string OutboxEventType = "me.practitioner-profile-updated";

    public sealed record Payload(
        Guid KeycloakUserId,
        Guid OrganizationId,
        string PractitionerFhirId);
}
