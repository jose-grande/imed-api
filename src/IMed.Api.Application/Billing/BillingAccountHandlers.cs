using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;

namespace IMed.Api.Application.Billing;

public sealed record BillingAccountDto(
    Guid Id,
    string Kind,
    Guid PartyId,
    string? PartyName,
    string? PatientFhirId,
    Guid? GuarantorPartyId,
    string? EncounterFhirId,
    string Status,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    string Origin = "Manual",
    string? OriginRef = null,
    AccountBillingDto? Billing = null);

/// <summary>
/// Qué falta hacer con la cuenta. Viene en los listados, no en el estado de cuenta.
/// </summary>
/// <param name="Situation">
/// La situación principal, para la etiqueta: <c>ToInvoice</c>, <c>ToCollect</c>, <c>Settled</c>,
/// <c>Empty</c> o <c>Voided</c>. "Por facturar" gana sobre "por cobrar" porque es el paso que va
/// antes; <paramref name="ToInvoice"/> y <paramref name="ToCollect"/> dicen si están las dos.
/// </param>
/// <param name="PendingToInvoice">Total de los cargos sin facturar.</param>
/// <param name="PricePending">Algún cargo sin facturar todavía no tiene precio: no se puede facturar.</param>
/// <param name="BalanceDue">Saldo de las facturas emitidas.</param>
/// <param name="InConsultation">
/// La consulta de la cuenta sigue en curso. Solo se calcula si se pide: lo sabe HAPI, no la base.
/// Se cobra igual (D4) —el paciente puede pagar antes de pasar al laboratorio—, pero lo que el
/// médico agregue al cerrar aparecerá como pendiente nuevo.
/// </param>
public sealed record AccountBillingDto(
    string Situation,
    bool ToInvoice,
    bool ToCollect,
    decimal PendingToInvoice,
    bool PricePending,
    decimal BalanceDue,
    int OpenInvoices,
    bool? InConsultation = null);

/// <param name="Text">Nombre o RTN del cliente, número de orden o id del paciente.</param>
/// <param name="OpenedFrom">Día de la clínica desde el que se abrió (inclusive).</param>
/// <param name="OpenedTo">Día de la clínica hasta el que se abrió (inclusive).</param>
public sealed record SearchAccountsQuery(
    Guid OrganizationId,
    string? PatientFhirId = null,
    string? Status = null,
    string? Situation = null,
    string? Origin = null,
    string? Text = null,
    Guid? LocationId = null,
    DateOnly? OpenedFrom = null,
    DateOnly? OpenedTo = null,
    int Limit = 50,
    bool WithConsultationStatus = false);

/// <summary>
/// El estado de cuenta (pantalla B6). Es la pantalla vertebral del módulo: cualquier otra que
/// hable de dinero enlaza acá.
/// </summary>
public sealed record AccountStatementDto(
    BillingAccountDto Account,
    IReadOnlyList<ChargeDto> Charges,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal Total);

public sealed class GetAccountStatementHandler(
    IBillingAccountRepository accounts, IPartyRepository parties)
{
    public async Task<AccountStatementDto?> HandleAsync(
        Guid orgId, Guid accountId, CancellationToken ct = default)
    {
        var cuenta = await accounts.GetAccountAsync(orgId, accountId, ct);
        if (cuenta is null)
        {
            return null;
        }

        var cargos = await accounts.ListChargesAsync(orgId, accountId, ct);
        var parte = await parties.GetAsync(orgId, cuenta.PartyId, ct);

        // Los reversados se muestran —el movimiento tiene que poder explicarse— pero no suman.
        var vivos = cargos.Where(c => c.Status != ChargeStatus.Reversed).ToList();

        return new AccountStatementDto(
            ToDto(cuenta, parte?.LegalName),
            cargos.Select(c => ChargeMapper.ToDto(c)).ToList(),
            Subtotal: vivos.Sum(c => Math.Round(c.Quantity * c.UnitPrice, 2, MidpointRounding.AwayFromZero)),
            DiscountTotal: vivos.Sum(c => c.DiscountAmount),
            TaxTotal: vivos.Sum(c => c.TaxAmount),
            Total: vivos.Sum(c => c.Total));
    }

    internal static BillingAccountDto ToDto(BillingAccount a, string? partyName) =>
        new(a.Id, a.Kind.ToString(), a.PartyId, partyName, a.PatientFhirId,
            a.GuarantorPartyId, a.EncounterFhirId, a.Status.ToString(), a.OpenedAt, a.ClosedAt,
            a.Origin.ToString(), a.OriginRef);
}

/// <summary>
/// Cuentas con su situación de cobro. Sirve a la bandeja de Cuentas y a la Caja ("por cobrar"):
/// las dos preguntan lo mismo —qué falta hacer— y por eso usan la misma consulta.
/// </summary>
public sealed class SearchAccountsHandler(
    IBillingAccountRepository accounts, IPartyRepository parties, IClinicClock clock, IHapiFhirClient hapi)
{
    /// <summary>Estados de un Encounter en los que la consulta sigue abierta.</summary>
    private static readonly HashSet<string> EnCurso = ["in-progress", "on-hold", "arrived", "triaged"];

    /// <summary>Tope de lecturas a HAPI por listado: la marca es una ayuda, no puede frenar la Caja.</summary>
    private const int MaxConsultas = 50;

    public async Task<IReadOnlyList<BillingAccountDto>> HandleAsync(
        SearchAccountsQuery query, CancellationToken ct = default)
    {
        var orgId = query.OrganizationId;
        var rows = await accounts.SearchAccountsAsync(new AccountSearchCriteria(
            orgId,
            query.PatientFhirId,
            Parse<BillingAccountStatus>(query.Status),
            Parse<AccountBillingSituation>(query.Situation),
            Parse<BillingAccountOrigin>(query.Origin),
            string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim(),
            query.LocationId,
            query.OpenedFrom is { } desde ? clock.StartOf(desde) : null,
            query.OpenedTo is { } hasta ? clock.StartOf(hasta.AddDays(1)) : null,
            query.Limit), ct);

        // Nombres y cargos se resuelven en un barrido: una consulta por fila convertiría un
        // listado de 50 cuentas en 50 viajes a la base.
        var nombres = await parties.GetNamesAsync(orgId, rows.Select(r => r.Account.PartyId).Distinct().ToList(), ct);
        var conCargos = rows.Where(r => r.HasUninvoicedCharges).Select(r => r.Account.Id).ToList();
        var porFacturar = (await accounts.ListCapturedChargesAsync(orgId, conCargos, ct))
            .GroupBy(c => c.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(c => c.Total));

        var enConsulta = query.WithConsultationStatus
            ? await ConsultasEnCursoAsync(orgId, rows, ct)
            : null;

        return rows
            .Select(r =>
            {
                var resumen = Summarize(r, porFacturar.GetValueOrDefault(r.Account.Id));
                if (enConsulta is not null && r.Account.EncounterFhirId is { } enc
                    && (r.HasUninvoicedCharges || r.OpenInvoiceCount > 0))
                {
                    resumen = resumen with { InConsultation = enConsulta.Contains(enc) };
                }
                return GetAccountStatementHandler.ToDto(r.Account, nombres.GetValueOrDefault(r.Account.PartyId))
                    with { Billing = resumen };
            })
            .ToList();
    }

    /// <summary>
    /// Encuentros todavía en curso entre los de las cuentas pendientes. Se leen en paralelo y con
    /// tope; si HAPI no responde, la cuenta sale sin la marca en vez de romper el listado.
    /// </summary>
    private async Task<HashSet<string>> ConsultasEnCursoAsync(
        Guid orgId, IReadOnlyList<AccountSearchRow> rows, CancellationToken ct)
    {
        var encuentros = rows
            .Where(r => r.Account.EncounterFhirId is not null && (r.HasUninvoicedCharges || r.OpenInvoiceCount > 0))
            .Select(r => r.Account.EncounterFhirId!)
            .Distinct()
            .Take(MaxConsultas)
            .ToList();
        var particion = $"org-{orgId}";
        var estados = await Task.WhenAll(encuentros.Select(async id =>
        {
            try
            {
                var e = await hapi.ReadResourceAsync("Encounter", id, particion, ct);
                return (id, estado: e?["status"]?.GetValue<string>());
            }
            catch (HttpRequestException)
            {
                return (id, estado: (string?)null);
            }
        }));
        return estados
            .Where(x => x.estado is not null && EnCurso.Contains(x.estado))
            .Select(x => x.id)
            .ToHashSet(StringComparer.Ordinal);
    }

    internal static AccountBillingDto Summarize(AccountSearchRow r, decimal pendingToInvoice)
    {
        var anulada = r.Account.Status == BillingAccountStatus.Voided;
        var facturar = !anulada && r.HasUninvoicedCharges;
        var cobrar = r.OpenInvoiceCount > 0;
        var situacion = anulada ? AccountBillingSituation.Voided
            : facturar ? AccountBillingSituation.ToInvoice
            : cobrar ? AccountBillingSituation.ToCollect
            : r.HasInvoices ? AccountBillingSituation.Settled
            : AccountBillingSituation.Empty;
        return new AccountBillingDto(
            situacion.ToString(), facturar, cobrar, pendingToInvoice,
            facturar && r.HasPricePendingCharges, r.BalanceDue, r.OpenInvoiceCount);
    }

    private static T? Parse<T>(string? value) where T : struct, Enum =>
        Enum.TryParse<T>(value, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : null;
}

public sealed record OpenAccountCommand(
    Guid OrganizationId,
    Guid? PartyId,
    string? PatientFhirId,
    string? PatientName,
    string? EncounterFhirId,
    string? Kind);

/// <summary>
/// Abre una cuenta, o devuelve la que ya existe para ese encuentro.
///
/// <para><b>Es idempotente por encuentro</b> porque el cierre de una consulta puede reintentarse:
/// sin esto, dos intentos dejan dos cuentas del mismo episodio y el estado de cuenta del paciente
/// queda partido en dos sin que nada lo señale.</para>
///
/// <para>Si el paciente no tiene <c>party</c>, se le crea una: obligar a darlo de alta aparte
/// convierte "cobrar una consulta" en dos trámites, y el segundo se olvida.</para>
/// </summary>
public sealed class OpenAccountHandler(
    IBillingAccountRepository accounts, IPartyRepository parties)
{
    public async Task<BillingAccountDto> HandleAsync(OpenAccountCommand cmd, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(cmd.EncounterFhirId))
        {
            var existente = await accounts.FindByEncounterAsync(cmd.OrganizationId, cmd.EncounterFhirId, ct);
            if (existente is not null)
            {
                var parteExistente = await parties.GetAsync(cmd.OrganizationId, existente.PartyId, ct);
                return GetAccountStatementHandler.ToDto(existente, parteExistente?.LegalName);
            }
        }

        var partyId = cmd.PartyId ?? await ResolverParteAsync(cmd, ct);
        var parte = await parties.GetAsync(cmd.OrganizationId, partyId, ct);

        var cuenta = BillingAccount.Open(
            cmd.OrganizationId, partyId,
            Enum.TryParse<BillingAccountKind>(cmd.Kind, ignoreCase: true, out var k)
                ? k : BillingAccountKind.Episode,
            cmd.PatientFhirId, cmd.EncounterFhirId);

        await accounts.AddAccountAsync(cuenta, ct);
        return GetAccountStatementHandler.ToDto(cuenta, parte?.LegalName);
    }

    private async Task<Guid> ResolverParteAsync(OpenAccountCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.PatientFhirId))
        {
            throw new ArgumentException(
                "Para abrir una cuenta hace falta la parte o el paciente.", nameof(cmd));
        }

        var existente = await parties.FindByPatientAsync(cmd.OrganizationId, cmd.PatientFhirId, ct);
        if (existente is not null)
        {
            return existente.Id;
        }

        var nueva = Party.Create(
            cmd.OrganizationId, PartyKind.Patient,
            legalName: string.IsNullOrWhiteSpace(cmd.PatientName) ? "Paciente" : cmd.PatientName,
            patientFhirId: cmd.PatientFhirId);
        await parties.AddAsync(nueva, ct);
        return nueva.Id;
    }
}

public sealed class CloseAccountHandler(IBillingAccountRepository accounts)
{
    public async Task<bool> HandleAsync(Guid orgId, Guid accountId, CancellationToken ct = default)
    {
        var cuenta = await accounts.GetAccountAsync(orgId, accountId, ct);
        if (cuenta is null)
        {
            return false;
        }
        cuenta.Close();
        await accounts.UpdateAccountAsync(cuenta, ct);
        return true;
    }
}
