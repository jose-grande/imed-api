using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Invitations;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Organizations;
using IMed.Api.Domain.Practitioners;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Team;

/// <summary>Lo que el token dice del usuario que inicia sesión.</summary>
public sealed record StartSessionCommand(
    Guid KeycloakUserId,
    Guid OrganizationId,
    string? Email,
    string? DisplayName,
    string? GivenName,
    string? FamilyName,
    IReadOnlyList<string> Roles);

/// <param name="HasClinicalProfile">Tiene Practitioner en la organización.</param>
/// <param name="ClinicalProfilePending">
/// Le corresponde un Practitioner pero HAPI no respondió: se reintenta en el próximo ingreso.
/// </param>
public sealed record StartSessionResult(bool HasClinicalProfile, bool ClinicalProfilePending);

/// <summary>El miembro fue dado de baja por un administrador.</summary>
public sealed class MemberInactiveException() : Exception(
    "Tu acceso a esta organización fue desactivado. Comunicate con el administrador.");

/// <summary>
/// Registro del usuario en su organización al iniciar sesión (<c>POST /api/me/session</c>). El
/// portal lo llama una vez por sesión, antes de cargar el menú. Idempotente.
///
/// <list type="number">
///   <item><b>Membresía.</b> Si el usuario no es miembro, se lo registra; si ya lo es, se
///   refrescan nombre, correo y último ingreso. Un miembro dado de baja recibe 403. Así aparece
///   en Equipo cualquier cuenta con <c>organization_id</c>, también una creada desde la consola
///   de Keycloak.</item>
///   <item><b>Invitación pendiente.</b> Se completa acá, y no al abrir la página de perfil como
///   antes: un invitado que nunca abría su perfil quedaba sin registrar.</item>
///   <item><b>Perfil clínico.</b> Solo para roles clínicos y de laboratorio
///   (<see cref="ClinicalRoles"/>): Practitioner + PractitionerRole en HAPI y su vínculo local.
///   Si a alguien le asignan uno de esos roles después, el perfil se crea en su siguiente ingreso
///   (los roles nuevos llegan en el token nuevo de todos modos).</item>
/// </list>
///
/// <para>Una falla de HAPI no impide entrar: la membresía ya quedó, y el perfil clínico se
/// reintenta en el próximo ingreso.</para>
/// </summary>
public sealed class StartSessionHandler(
    IOrganizationMemberRepository members,
    IUserPractitionerAssignmentRepository assignments,
    IInvitationsRepository invitations,
    InvitationLazyProvisionService invitationProvision,
    IOrganizationProfileRepository organizations,
    IHapiFhirClient hapi,
    IKeycloakAdminClient keycloak,
    ILogger<StartSessionHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<StartSessionResult> HandleAsync(StartSessionCommand cmd, CancellationToken ct = default)
    {
        // 1. Membresía.
        var member = await members.GetAsync(cmd.KeycloakUserId, cmd.OrganizationId, ct);
        if (member is null)
        {
            member = OrganizationMember.Join(
                cmd.KeycloakUserId, cmd.OrganizationId, cmd.DisplayName, cmd.Email, timeProvider);
            await members.AddAsync(member, ct);
            logger.LogInformation(
                "Sesión: {UserId} registrado como miembro de {OrgId}", cmd.KeycloakUserId, cmd.OrganizationId);
        }
        else
        {
            if (!member.IsActive)
            {
                throw new MemberInactiveException();
            }
            member.SeenAt(cmd.DisplayName, cmd.Email, timeProvider);
            await members.UpdateAsync(member, ct);
        }

        var needsProfile = ClinicalRoles.RequirePractitioner(cmd.Roles);
        var assignment = await assignments.GetAsync(cmd.KeycloakUserId, cmd.OrganizationId, ct);
        if (assignment is not null)
        {
            return new StartSessionResult(HasClinicalProfile: true, ClinicalProfilePending: false);
        }

        // 2. Invitación pendiente.
        var pending = string.IsNullOrWhiteSpace(cmd.Email)
            ? null
            : await invitations.GetPendingByEmailAndOrganizationAsync(cmd.Email, cmd.OrganizationId, ct);
        if (pending is not null)
        {
            if (needsProfile)
            {
                var lazy = await invitationProvision.CompleteAsync(
                    pending, cmd.KeycloakUserId, cmd.GivenName, cmd.FamilyName, cmd.Email, ct);
                return new StartSessionResult(lazy.Completed, ClinicalProfilePending: !lazy.Completed);
            }

            // Sin rol clínico la invitación se acepta sin crear un profesional FHIR.
            pending.Accept(cmd.KeycloakUserId, timeProvider);
            await invitations.UpdateAsync(pending, ct);
            logger.LogInformation(
                "Sesión: invitación {InvitationId} aceptada por {UserId} sin perfil clínico",
                pending.Id, cmd.KeycloakUserId);
            return new StartSessionResult(HasClinicalProfile: false, ClinicalProfilePending: false);
        }

        // 3. Perfil clínico para quien no vino por invitación (cuenta creada en Keycloak, rol
        //    clínico asignado después, administrador que además atiende).
        if (!needsProfile)
        {
            return new StartSessionResult(HasClinicalProfile: false, ClinicalProfilePending: false);
        }
        var created = await TryCreateClinicalProfileAsync(cmd, ct);
        return new StartSessionResult(HasClinicalProfile: created, ClinicalProfilePending: !created);
    }

    private async Task<bool> TryCreateClinicalProfileAsync(StartSessionCommand cmd, CancellationToken ct)
    {
        var org = await organizations.GetByIdAsync(cmd.OrganizationId, ct);
        if (org is null)
        {
            logger.LogWarning("Sesión: organización {OrgId} sin perfil; no se crea Practitioner", cmd.OrganizationId);
            return false;
        }
        var partition = $"org-{cmd.OrganizationId}";
        var email = cmd.Email ?? string.Empty;

        try
        {
            var practitionerId = await hapi.CreatePractitionerAsync(new UpsertPractitionerRequest(
                PartitionName: partition,
                Family: FirstNonEmpty(cmd.FamilyName, LocalPart(email), "Sin apellido"),
                Given: [FirstNonEmpty(cmd.GivenName, cmd.DisplayName, "Sin nombre")],
                Prefix: null,
                LicenseNumber: null,
                Email: string.IsNullOrWhiteSpace(email) ? null : email,
                Phone: null,
                Gender: null), ct);

            var roleId = await hapi.CreatePractitionerRoleAsync(new UpsertPractitionerRoleRequest(
                PartitionName: partition,
                PractitionerFhirId: practitionerId,
                OrganizationFhirId: org.FhirId,
                SpecialtyCode: null,
                SpecialtyDisplay: null,
                SpecialtySystem: "http://snomed.info/sct"), ct);

            await assignments.AddAsync(UserPractitionerAssignment.Create(
                cmd.KeycloakUserId, cmd.OrganizationId, practitionerId, roleId, timeProvider), ct);

            // El claim practitioner_id del token sale de este atributo. Si falla, el perfil existe
            // igual: el vínculo local es la fuente de verdad y el atributo se puede reponer.
            try
            {
                await keycloak.SetUserAttributesAsync(
                    cmd.KeycloakUserId,
                    new Dictionary<string, string[]> { ["practitioner_id"] = [practitionerId] },
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sesión: no se pudo escribir practitioner_id en Keycloak para {UserId}", cmd.KeycloakUserId);
            }

            logger.LogInformation(
                "Sesión: perfil clínico creado para {UserId} en {OrgId} (Practitioner {PractitionerId})",
                cmd.KeycloakUserId, cmd.OrganizationId, practitionerId);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Sesión: no se pudo crear el perfil clínico de {UserId} en {OrgId}; se reintenta en el próximo ingreso",
                cmd.KeycloakUserId, cmd.OrganizationId);
            return false;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(v => !string.IsNullOrWhiteSpace(v))!.Trim();

    private static string? LocalPart(string email)
    {
        var local = email.Split('@')[0];
        return local.Length == 0 ? null : char.ToUpperInvariant(local[0]) + local[1..];
    }
}
