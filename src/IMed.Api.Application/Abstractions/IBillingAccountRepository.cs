using IMed.Api.Domain.Billing.Accounts;

namespace IMed.Api.Application.Abstractions;

public interface IBillingAccountRepository
{
    Task<BillingAccount?> GetAccountAsync(Guid organizationId, Guid accountId, CancellationToken ct = default);

    /// <summary>Cuenta de episodio de un encuentro, si ya existe.</summary>
    Task<BillingAccount?> FindByEncounterAsync(
        Guid organizationId, string encounterFhirId, CancellationToken ct = default);

    Task<IReadOnlyList<BillingAccount>> SearchAccountsAsync(
        Guid organizationId, string? patientFhirId, string? status, int limit, CancellationToken ct = default);

    Task AddAccountAsync(BillingAccount account, CancellationToken ct = default);
    Task UpdateAccountAsync(BillingAccount account, CancellationToken ct = default);

    Task<IReadOnlyList<BillingCharge>> ListChargesAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default);

    Task<BillingCharge?> GetChargeAsync(Guid organizationId, Guid chargeId, CancellationToken ct = default);

    /// <summary>
    /// Lee un cargo <b>para modificarlo</b>. Va aparte de <see cref="GetChargeAsync"/> porque
    /// leer sin rastreo y después pedir un <c>Update</c> choca con la instancia que el contexto ya
    /// tenga de esa fila —por ejemplo, la que acaba de insertarse en la misma unidad de trabajo—.
    /// Declarar la intención al leer evita el conflicto en vez de destrabarlo después.
    /// </summary>
    Task<BillingCharge?> GetChargeForUpdateAsync(
        Guid organizationId, Guid chargeId, CancellationToken ct = default);

    /// <summary>
    /// Cargo vivo (no reversado) de un acto concreto. Es la lectura de idempotencia: contesta
    /// "¿este acto ya se cobró?" antes de intentar cobrarlo.
    /// </summary>
    Task<BillingCharge?> FindLiveChargeBySourceAsync(
        Guid organizationId,
        ChargeSourceKind sourceKind,
        string sourceId,
        Guid billableItemId,
        CancellationToken ct = default);

    /// <summary>
    /// Inserta el cargo. Devuelve <c>false</c> si el índice de idempotencia lo rechazó — es decir,
    /// si otro camino ya cobró ese mismo acto entre la lectura y la escritura.
    /// </summary>
    Task<bool> TryAddChargeAsync(BillingCharge charge, CancellationToken ct = default);

    Task UpdateChargeAsync(BillingCharge charge, CancellationToken ct = default);
}
