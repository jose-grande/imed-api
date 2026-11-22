using IMed.Api.Domain.Billing.Cash;

namespace IMed.Api.Application.Abstractions;

/// <summary>POS y cuentas bancarias de las sedes (ver <see cref="PaymentTerminal"/>). Todo por organización.</summary>
public interface IPaymentTerminalRepository
{
    /// <summary>Los de la organización, o los de una sede. Inactivos incluidos: los cierres los nombran.</summary>
    Task<IReadOnlyList<PaymentTerminal>> ListAsync(
        Guid organizationId, Guid? locationId = null, CancellationToken ct = default);

    Task<PaymentTerminal?> GetAsync(Guid organizationId, Guid terminalId, CancellationToken ct = default);

    Task AddAsync(PaymentTerminal terminal, CancellationToken ct = default);

    Task UpdateAsync(PaymentTerminal terminal, CancellationToken ct = default);
}
