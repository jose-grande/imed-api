using IMed.Api.Domain.Billing.Cash;
using IMed.Api.Domain.Billing.Invoicing;

namespace IMed.Api.Application.Abstractions;

public interface ICashRepository
{
    Task<CashSession?> GetSessionAsync(Guid organizationId, Guid sessionId, CancellationToken ct = default);

    /// <summary>Turno abierto de una caja, si lo hay.</summary>
    Task<CashSession?> FindOpenSessionByPointAsync(
        Guid organizationId, Guid cashPointId, CancellationToken ct = default);

    /// <summary>Turno abierto de un cajero, sin importar en qué caja.</summary>
    /// <summary>
    /// Turnos abiertos de la organización, uno por caja como máximo. Lo usa la lista de cajas para
    /// mostrar cuál está operando: "habilitada" dice que se puede usar, no que alguien la esté usando.
    /// </summary>
    Task<IReadOnlyList<CashSession>> ListOpenSessionsAsync(
        Guid organizationId, CancellationToken ct = default);

    /// <summary>Turnos abiertos dentro de un rango de instantes (para el reporte de cajas).</summary>
    Task<IReadOnlyList<CashSession>> ListSessionsOpenedBetweenAsync(
        Guid organizationId, DateTimeOffset from, DateTimeOffset before, CancellationToken ct = default);

    /// <summary>Pagos de varios turnos en un solo viaje, con sus asignaciones.</summary>
    Task<IReadOnlyList<Payment>> ListPaymentsForSessionsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default);

    /// <summary>Movimientos de efectivo de varios turnos en un solo viaje.</summary>
    Task<IReadOnlyList<CashMovement>> ListMovementsForSessionsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default);

    Task<CashSession?> FindOpenSessionByCashierAsync(
        Guid organizationId, Guid cashierUserId, CancellationToken ct = default);

    Task<IReadOnlyList<CashSession>> ListSessionsAsync(
        Guid organizationId, Guid? cashPointId, DateOnly? day, CancellationToken ct = default);

    Task AddSessionAsync(CashSession session, CancellationToken ct = default);

    /// <summary>Guarda los cambios de un turno existente — cerrarlo, sobre todo.</summary>
    Task UpdateSessionAsync(CashSession session, CancellationToken ct = default);

    /// <summary>Turno leído <b>para modificarlo</b> — cerrarlo cambia varias columnas a la vez.</summary>
    Task<CashSession?> GetSessionForUpdateAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<CashMovement>> ListMovementsAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default);

    Task AddMovementAsync(CashMovement movement, CancellationToken ct = default);

    Task<IReadOnlyList<Payment>> ListPaymentsAsync(
        Guid organizationId, Guid sessionId, CancellationToken ct = default);

    Task<Payment?> GetPaymentAsync(Guid organizationId, Guid paymentId, CancellationToken ct = default);

    /// <summary>
    /// Guarda el cobro y el saldo de las facturas que toca, <b>en una transacción</b>.
    ///
    /// <para>Partirlo dejaría un pago registrado contra facturas que siguen figurando impagas —o
    /// al revés—, y las dos versiones aparecen al cierre del día sin forma de saber cuál es la
    /// verdadera.</para>
    /// </summary>
    Task SavePaymentAsync(
        Payment payment, IReadOnlyList<Invoice> settledInvoices, CancellationToken ct = default);
}
