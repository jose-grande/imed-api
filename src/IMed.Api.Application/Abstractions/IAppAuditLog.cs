using IMed.Api.Domain.Auditing;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Puerto para insertar entradas de auditoría app-level. La implementación
/// elige el contexto (DbContext compartido vs nuevo scope) según convenga al
/// caso de uso (sincrónico transaccional vs fire-and-forget).
/// </summary>
public interface IAppAuditLog
{
    Task RecordAsync(AppAuditLogEntry entry, CancellationToken cancellationToken = default);
}
