namespace IMed.Api.Application.Invitations;

/// <summary>
/// Outbox event que dispara <see cref="IKeycloakAdminClient.ExecuteActionsEmailAsync"/>
/// cuando el hot-path falló post-creación del KC user. El dispatcher es idempotente:
/// KC genera y envía un nuevo action token cada vez que se invoca.
/// </summary>
public static class InvitationEmailPendingEvent
{
    public const string OutboxEventType = "invitation.email-pending";

    public sealed record Payload(
        Guid InvitationId,
        Guid KeycloakUserId,
        string Email,
        IReadOnlyList<string> Actions,
        int LifespanSeconds,
        string? RedirectUri,
        string? ClientId);
}

/// <summary>
/// Outbox event para reintentar la creación del Practitioner+PractitionerRole en
/// HAPI cuando el lazy provision falla durante el primer login del invitee. El
/// dispatcher es idempotente: chequea si ya existe assignment antes de crear.
/// </summary>
public static class InvitationLazyProvisionPendingEvent
{
    public const string OutboxEventType = "invitation.lazy-provision-pending";

    public sealed record Payload(
        Guid InvitationId,
        Guid KeycloakUserId,
        Guid OrganizationId,
        string OrganizationFhirId,
        string? SpecialtyHint,
        string? Email,
        string? GivenName,
        string? FamilyName);
}
