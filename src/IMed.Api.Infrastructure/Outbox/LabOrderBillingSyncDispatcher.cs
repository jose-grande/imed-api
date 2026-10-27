using System.Text.Json;
using IMed.Api.Application.Billing;
using IMed.Api.Application.Outbox;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Outbox;

/// <summary>Despacha <c>lab-order.billing-sync</c>: reconcilia el cargo de la orden (A5).</summary>
public sealed class LabOrderBillingSyncDispatcher(
    LabOrderChargeReconciler reconciler,
    ILogger<LabOrderBillingSyncDispatcher> logger) : IOutboxDispatcher
{
    public string EventType => LabOrderBillingSyncEvent.OutboxEventType;

    public async Task DispatchAsync(string payloadJson, CancellationToken cancellationToken)
    {
        LabOrderBillingSyncEvent.Payload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<LabOrderBillingSyncEvent.Payload>(payloadJson);
        }
        catch (JsonException ex)
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' no es JSON válido.", ex);
        }
        if (payload is null || string.IsNullOrWhiteSpace(payload.ServiceRequestId))
        {
            throw new OutboxPermanentFailureException($"Payload de '{EventType}' incompleto.");
        }

        var resultado = await reconciler.ReconcileAsync(payload.OrganizationId, payload.ServiceRequestId, cancellationToken);
        logger.LogInformation("Cobro de orden {Order}: {Resultado}", payload.ServiceRequestId, resultado);
    }
}
