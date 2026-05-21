using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Invitations;
using IMed.Api.Application.Outbox;
using IMed.Api.Domain.Invitations;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha eventos <c>invitation.lazy-provision-pending</c>: re-ejecuta el
/// lazy provision (HAPI Practitioner+PractitionerRole + assignment + accept
/// invitation + KC sync) cuando el primer intento durante el GET del perfil falló.
///
/// <para>Idempotente vía <see cref="InvitationLazyProvisionService"/> que detecta
/// si ya hay assignment para (user, org) y solo cierra el ciclo en BD si lo
/// hay, evitando dobles writes en HAPI.</para>
/// </summary>
public sealed class InvitationLazyProvisionPendingDispatcher(
    IInvitationsRepository invitationsRepo,
    InvitationLazyProvisionService lazyProvisionService,
    ILogger<InvitationLazyProvisionPendingDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => InvitationLazyProvisionPendingEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        InvitationLazyProvisionPendingEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<InvitationLazyProvisionPendingEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException(
                $"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' es null.");
        }

        // El AsNoTracking del repo entrega un detached entity — usable para
        // pasar al lazy provision service que lo va a Update-ar dentro de
        // la transacción (o lo va a aceptar in-place).
        var invitation = await invitationsRepo.GetByIdAsync(payload.InvitationId, cancellationToken)
            ?? throw new OutboxPermanentFailureException(
                $"Invitation {payload.InvitationId} no existe.");

        if (invitation.Status == InvitationStatus.Revoked || invitation.Status == InvitationStatus.Expired)
        {
            // Estado terminal alcanzado por otra vía — el evento no aplica.
            // Marcar permanent para no acumular retries.
            throw new OutboxPermanentFailureException(
                $"Invitation {payload.InvitationId} está {invitation.Status}; no se aplica lazy provision.");
        }

        var result = await lazyProvisionService.CompleteAsync(
            invitation,
            payload.KeycloakUserId,
            payload.GivenName,
            payload.FamilyName,
            payload.Email,
            cancellationToken);

        if (!result.Completed)
        {
            // HAPI sigue caído; el service ya escribió otro outbox event para
            // el siguiente retry. Throw para que este event quede pendiente en
            // su backoff schedule y el processor reintente.
            throw new InvalidOperationException(
                $"Lazy provision sigue fallando para invitation {payload.InvitationId}; " +
                "HAPI no respondió OK.");
        }

        logger.LogInformation(
            "Outbox dispatch OK: invitation.lazy-provision-pending invitation={InvitationId} user={UserId} practitioner={PractitionerId}",
            payload.InvitationId, payload.KeycloakUserId, result.PractitionerFhirId);
    }
}
