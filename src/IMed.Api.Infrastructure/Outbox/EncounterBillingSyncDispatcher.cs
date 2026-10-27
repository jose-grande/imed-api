using System.Text.Json;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Outbox;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>Despacha <c>encounter.billing-sync</c>: reconcilia el cargo de la consulta (A4).</summary>
public sealed class EncounterBillingSyncDispatcher(
    EncounterChargeReconciler reconciler,
    ILogger<EncounterBillingSyncDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => EncounterBillingSyncEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        EncounterBillingSyncEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<EncounterBillingSyncEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.EncounterId))
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' incompleto.");
        }

        var resultado = await reconciler.ReconcileAsync(payload.OrganizationId, payload.EncounterId, cancellationToken);
        logger.LogInformation("Cobro de consulta {Encounter}: {Resultado}", payload.EncounterId, resultado);
    }
}
