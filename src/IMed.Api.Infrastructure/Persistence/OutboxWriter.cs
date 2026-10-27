using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Outbox;

namespace IMed.Api.Infrastructure.Persistence;

public sealed class OutboxWriter(AppDbContext db, TimeProvider timeProvider) : IOutboxWriter
{
    public async Task EnqueueAsync(string eventType, string aggregate, string payloadJson, CancellationToken ct = default)
    {
        db.OutboxEvents.Add(OutboxEvent.Create(aggregate: aggregate, type: eventType, payloadJson: payloadJson, timeProvider: timeProvider));
        await db.SaveChangesAsync(ct);
    }
}
