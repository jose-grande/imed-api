using IMed.Api.Domain.Billing.Accounts;

namespace IMed.Api.Application.Abstractions;

public interface IBillingAccountRepository
{
    Task<BillingAccount?> GetAccountAsync(Guid organizationId, Guid accountId, CancellationToken ct = default);

    /// <summary>Cuenta de episodio de un encuentro, si ya existe.</summary>
    Task<BillingAccount?> FindByEncounterAsync(
        Guid organizationId, string encounterFhirId, CancellationToken ct = default);

    /// <summary>
    /// Busca cuentas con su situación de cobro calculada en la misma consulta. El filtro de
    /// situación se aplica antes del límite: si se aplicara después, "por cobrar" devolvería las
    /// que tuvieron la suerte de caer entre las primeras N.
    /// </summary>
    Task<IReadOnlyList<AccountSearchRow>> SearchAccountsAsync(
        AccountSearchCriteria criteria, CancellationToken ct = default);

    /// <summary>Cargos capturados (sin facturar) de varias cuentas, en un solo viaje.</summary>
    Task<IReadOnlyList<BillingCharge>> ListCapturedChargesAsync(
        Guid organizationId, IReadOnlyCollection<Guid> accountIds, CancellationToken ct = default);

    Task AddAccountAsync(BillingAccount account, CancellationToken ct = default);

    /// <summary>
    /// Agrega la cuenta salvo que otra ya ocupe su encuentro o su acto de origen. Devuelve
    /// <c>false</c> si perdió la carrera: el índice único decide.
    /// </summary>
    Task<bool> TryAddAccountAsync(BillingAccount account, CancellationToken ct = default);

    /// <summary>La cuenta abierta por un acto de origen, en cualquier estado.</summary>
    Task<BillingAccount?> FindByOriginAsync(
        Guid organizationId, BillingAccountOrigin origin, string originRef, CancellationToken ct = default);
    Task UpdateAccountAsync(BillingAccount account, CancellationToken ct = default);

    Task<IReadOnlyList<BillingCharge>> ListChargesAsync(
        Guid organizationId, Guid accountId, CancellationToken ct = default);

    /// <summary>
    /// Cargos capturados de una cuenta, leídos <b>para modificarlos</b> — la emisión los marca
    /// como facturados. Misma razón que <see cref="GetChargeForUpdateAsync"/>: adjuntar una
    /// instancia distinta de una fila que el contexto ya rastrea es un conflicto de identidad.
    /// </summary>
    Task<IReadOnlyList<BillingCharge>> ListCapturedChargesForUpdateAsync(
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
    /// <summary>
    /// Cualquier cargo vivo de un acto, sin importar qué cobrable sea. Lo usa la puerta de
    /// prepago: pregunta "¿este examen está pagado?" sin saber a qué ítem del catálogo se enlazó.
    /// </summary>
    Task<BillingCharge?> FindAnyLiveChargeBySourceAsync(
        Guid organizationId,
        ChargeSourceKind sourceKind,
        string sourceId,
        CancellationToken ct = default);

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

    /// <summary>Cargos no reversados de un acto (todos sus cobrables), rastreados para modificarlos.</summary>
    Task<IReadOnlyList<BillingCharge>> ListLiveChargesBySourceForUpdateAsync(
        Guid organizationId, ChargeSourceKind sourceKind, string sourceId, CancellationToken ct = default);

    /// <summary>
    /// ¿El paciente tuvo una consulta (cargo de encuentro, no reversado) de esa especialidad desde
    /// <paramref name="since"/>? Es lo que propone "control" en vez de "primera vez" (P1).
    /// </summary>
    Task<bool> HasConsultationSinceAsync(
        Guid organizationId, string patientFhirId, string specialtyCode, DateTimeOffset since,
        string? excludeEncounterFhirId, CancellationToken ct = default);

    Task UpdateChargeAsync(BillingCharge charge, CancellationToken ct = default);

    /// <summary>
    /// Cargos capturados sin precio de un cobrable, rastreados para modificarlos. Los usa fijar el
    /// precio del cobrable: los cargos que esperaban ese precio lo toman en el mismo SaveChanges
    /// que guarda el cobrable (comparten la unidad de trabajo).
    /// </summary>
    Task<IReadOnlyList<BillingCharge>> ListPricePendingChargesForUpdateAsync(
        Guid organizationId, Guid billableItemId, CancellationToken ct = default);

    /// <summary>
    /// Para cada cita con consulta cobrada por adelantado (cargo vivo con origen cita): lo que falta
    /// pagar de su factura, o <c>null</c> si todavía no se facturó. Las citas sin cargo no vienen.
    /// </summary>
    Task<IReadOnlyDictionary<string, decimal?>> PrepaidAppointmentBalancesAsync(
        Guid organizationId, IReadOnlyCollection<string> appointmentIds, CancellationToken ct = default);
}

/// <summary>
/// Situación de cobro de una cuenta. <b>Se calcula</b> de sus cargos y facturas, no se guarda: el
/// estado guardado (<see cref="BillingAccountStatus"/>) dice si la cuenta acepta cargos, no si hay
/// algo que cobrar, y una cuenta abierta puede tener a la vez cargos sin facturar y una factura con
/// saldo.
/// </summary>
public enum AccountBillingSituation
{
    /// <summary>Tiene cargos capturados que todavía no salieron en una factura.</summary>
    ToInvoice,

    /// <summary>Tiene facturas emitidas con saldo.</summary>
    ToCollect,

    /// <summary>Filtro: por facturar o por cobrar. Es la bandeja de trabajo.</summary>
    Pending,

    /// <summary>Todo facturado y pagado.</summary>
    Settled,

    /// <summary>Sin cargos vivos ni facturas.</summary>
    Empty,

    /// <summary>Cuenta anulada.</summary>
    Voided,
}

/// <param name="Text">Nombre o RTN del cliente, número de orden (ORD-…) o id del paciente.</param>
/// <param name="LocationId">Cuentas con algún cargo o factura en esa sede.</param>
public sealed record AccountSearchCriteria(
    Guid OrganizationId,
    string? PatientFhirId = null,
    BillingAccountStatus? Status = null,
    AccountBillingSituation? Situation = null,
    BillingAccountOrigin? Origin = null,
    string? Text = null,
    Guid? LocationId = null,
    DateTimeOffset? OpenedFrom = null,
    DateTimeOffset? OpenedBefore = null,
    int Limit = 50);

/// <summary>Una cuenta con lo necesario para saber qué falta hacer con ella.</summary>
public sealed record AccountSearchRow(
    BillingAccount Account,
    bool HasUninvoicedCharges,
    bool HasPricePendingCharges,
    bool HasLiveCharges,
    bool HasInvoices,
    decimal BalanceDue,
    int OpenInvoiceCount);
