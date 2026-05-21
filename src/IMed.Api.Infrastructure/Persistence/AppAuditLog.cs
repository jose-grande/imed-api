using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Infrastructure.Persistence;

/// <summary>
/// Implementación del puerto <see cref="IAppAuditLog"/>. Inserta en la misma
/// instancia de <see cref="AppDbContext"/> que el caso de uso (registrado scoped),
/// para que los registros vivan dentro de la misma transacción cuando aplica.
///
/// Si el caller necesita insertar fuera de transacción (ej. background work
/// que no comparte scope), debe abrir su propio scope vía <c>IServiceScopeFactory</c>.
/// </summary>
public sealed class AppAuditLog(
    AppDbContext dbContext,
    ILogger<AppAuditLog> logger) : IAppAuditLog
{
    public async Task RecordAsync(AppAuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        dbContext.AppAuditLog.Add(entry);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogDebug(
            "Audit log: category={Category}, action={Action}, actor={Actor}, org={Org}, target={Target}",
            entry.Category, entry.Action, entry.ActorUserId, entry.OrganizationId, entry.Target);
    }
}
