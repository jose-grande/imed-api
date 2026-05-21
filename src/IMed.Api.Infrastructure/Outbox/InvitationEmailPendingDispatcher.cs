using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Invitations;
using IMed.Api.Application.Outbox;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>
/// Despacha eventos <c>invitation.email-pending</c>: re-dispara
/// <c>executeActionsEmail</c> en KC cuando el hot-path del invite falló.
///
/// <para>Idempotente: KC genera un nuevo action token cada vez que se invoca;
/// invocaciones repetidas son seguras (el último email envía un token nuevo
/// con la lifespan original; los anteriores caducan por su propio TTL).</para>
/// </summary>
public sealed class InvitationEmailPendingDispatcher(
    IKeycloakAdminClient keycloakAdmin,
    ILogger<InvitationEmailPendingDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => InvitationEmailPendingEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        InvitationEmailPendingEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<InvitationEmailPendingEvent.Payload>(payloadJson);
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

        await keycloakAdmin.ExecuteActionsEmailAsync(
            payload.KeycloakUserId,
            payload.Actions,
            payload.LifespanSeconds,
            payload.RedirectUri,
            payload.ClientId,
            cancellationToken);

        logger.LogInformation(
            "Outbox dispatch OK: invitation.email-pending invitation={InvitationId} email={Email}",
            payload.InvitationId, payload.Email);
    }
}
