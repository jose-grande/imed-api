using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Practitioners;

/// <summary>
/// Query del GET /api/me/practitioner-profile. Incluye claims del JWT que el
/// lazy provision necesita: el email matchea el lookup de invitation pending,
/// y los nombres alimentan el Practitioner inicial en HAPI cuando se crea via
/// invitation acceptance.
/// </summary>
public sealed record GetMyPractitionerProfileQuery(
    Guid KeycloakUserId,
    Guid OrganizationId,
    string? Email,
    string? GivenName,
    string? FamilyName);

/// <summary>
/// Lee el perfil profesional del user en su org actual. Combina:
///   1. <c>user_practitioner_assignments</c> para el link estable (Practitioner FHIR id).
///   2. HAPI <c>Practitioner</c> + <c>PractitionerRole</c> para los datos visibles.
///
/// <para>Si no hay assignment, busca invitation pending por (email, org) y dispara
/// lazy provision (Épica F3): crea Practitioner+PractitionerRole en HAPI, persiste
/// assignment, marca invitation accepted, sincroniza KC. Si HAPI falla durante
/// el lazy provision, la respuesta devuelve <c>LazyProvisionInProgress=true</c>
/// para que la UI muestre spinner y reintente.</para>
///
/// <para>Si tampoco hay invitation pending, devuelve <see cref="PractitionerProfileDto"/>
/// con <c>Configured=false</c> (caso Org Admin recién bootstrap-eado).</para>
/// </summary>
public sealed class GetMyPractitionerProfileHandler(
    IUserPractitionerAssignmentRepository assignmentRepo,
    IHapiFhirClient hapiFhir,
    ISpecialtyRepository specialtyRepo,
    IInvitationsRepository invitationsRepo,
    InvitationLazyProvisionService lazyProvisionService,
    ILogger<GetMyPractitionerProfileHandler> logger)
{
    public async Task<PractitionerProfileDto> HandleAsync(
        GetMyPractitionerProfileQuery query,
        CancellationToken cancellationToken = default)
    {
        var assignment = await assignmentRepo.GetAsync(query.KeycloakUserId, query.OrganizationId, cancellationToken);
        if (assignment is null)
        {
            // Lazy provision: ¿este user llega vía invitation aceptada en KC?
            // Solo intentamos si el JWT trae email — sin email no podemos buscar
            // la invitation pending.
            if (!string.IsNullOrWhiteSpace(query.Email))
            {
                var pending = await invitationsRepo.GetPendingByEmailAndOrganizationAsync(
                    query.Email, query.OrganizationId, cancellationToken);
                if (pending is not null)
                {
                    logger.LogInformation(
                        "Lazy provision: invitation pending detectada para user {UserId} email {Email} org {OrgId}",
                        query.KeycloakUserId, query.Email, query.OrganizationId);

                    var lazyResult = await lazyProvisionService.CompleteAsync(
                        pending,
                        query.KeycloakUserId,
                        givenNameClaim: query.GivenName,
                        familyNameClaim: query.FamilyName,
                        emailClaim: query.Email,
                        cancellationToken);

                    if (!lazyResult.Completed)
                    {
                        return new PractitionerProfileDto(
                            Configured: false,
                            PractitionerFhirId: null,
                            PractitionerRoleFhirId: null,
                            Family: query.FamilyName,
                            Given: string.IsNullOrWhiteSpace(query.GivenName) ? null : new[] { query.GivenName },
                            Prefix: null,
                            LicenseNumber: null,
                            Email: query.Email,
                            Phone: null,
                            Gender: null,
                            SpecialtyCode: lazyResult.SpecialtyCode,
                            SpecialtyDisplay: lazyResult.SpecialtyDisplay,
                            LazyProvisionInProgress: true);
                    }

                    // Lazy provision OK — refrescar assignment para devolver el perfil
                    // completo en la misma respuesta.
                    assignment = await assignmentRepo.GetAsync(
                        query.KeycloakUserId, query.OrganizationId, cancellationToken);
                }
            }

            if (assignment is null)
            {
                return new PractitionerProfileDto(
                    Configured: false,
                    PractitionerFhirId: null,
                    PractitionerRoleFhirId: null,
                    Family: null,
                    Given: null,
                    Prefix: null,
                    LicenseNumber: null,
                    Email: null,
                    Phone: null,
                    Gender: null,
                    SpecialtyCode: null,
                    SpecialtyDisplay: null);
            }
        }

        var partitionName = $"org-{query.OrganizationId}";

        // Best-effort read del Practitioner. Si HAPI está caído o el recurso
        // fue borrado fuera de banda, devolvemos los datos del assignment con
        // los campos del Practitioner vacíos: la UI los muestra vacíos sin tirar error.
        PractitionerSnapshot? snapshot = null;
        try
        {
            snapshot = await hapiFhir.GetPractitionerAsync(
                assignment.PractitionerFhirId,
                partitionName,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "No se pudo leer Practitioner {Id} de HAPI; devolviendo perfil parcial.",
                assignment.PractitionerFhirId);
        }

        // Specialty: leer el PractitionerRole de HAPI si el assignment lo tiene.
        // El display se re-hidrata desde el catálogo local — más confiable que
        // el snapshot que quedó en HAPI (puede traer códigos viejos si la
        // traducción del catálogo cambió).
        string? specialtyCode = null;
        string? specialtyDisplay = null;
        if (!string.IsNullOrEmpty(assignment.PractitionerRoleFhirId))
        {
            try
            {
                var roleSnapshot = await hapiFhir.GetPractitionerRoleAsync(
                    assignment.PractitionerRoleFhirId,
                    partitionName,
                    cancellationToken);
                specialtyCode = roleSnapshot?.SpecialtyCode;
                if (!string.IsNullOrWhiteSpace(specialtyCode))
                {
                    var catalogEntry = await specialtyRepo.GetByCodeAsync(specialtyCode, cancellationToken);
                    specialtyDisplay = catalogEntry?.DisplayEs ?? roleSnapshot?.SpecialtyDisplay;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "No se pudo leer PractitionerRole {Id} de HAPI; devolviendo perfil sin specialty.",
                    assignment.PractitionerRoleFhirId);
            }
        }

        return new PractitionerProfileDto(
            Configured: true,
            PractitionerFhirId: assignment.PractitionerFhirId,
            PractitionerRoleFhirId: assignment.PractitionerRoleFhirId,
            Family: snapshot?.Family,
            Given: snapshot?.Given,
            Prefix: snapshot?.Prefix,
            LicenseNumber: snapshot?.LicenseNumber,
            Email: snapshot?.Email,
            Phone: snapshot?.Phone,
            Gender: snapshot?.Gender,
            SpecialtyCode: specialtyCode,
            SpecialtyDisplay: specialtyDisplay);
    }
}
