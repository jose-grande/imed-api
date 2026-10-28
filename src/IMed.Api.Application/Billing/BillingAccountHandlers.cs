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
    string? OriginRef = null);

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

public sealed class SearchAccountsHandler(
    IBillingAccountRepository accounts, IPartyRepository parties)
{
    public async Task<IReadOnlyList<BillingAccountDto>> HandleAsync(
        Guid orgId, string? patientFhirId, string? status, int limit, CancellationToken ct = default)
    {
        var rows = await accounts.SearchAccountsAsync(orgId, patientFhirId, status, limit, ct);

        // Los nombres se resuelven en un barrido: una consulta por fila convertiría un listado de
        // 50 cuentas en 50 viajes a la base.
        var ids = rows.Select(r => r.PartyId).Distinct().ToList();
        var nombres = await parties.GetNamesAsync(orgId, ids, ct);

        return rows
            .Select(a => GetAccountStatementHandler.ToDto(a, nombres.GetValueOrDefault(a.PartyId)))
            .ToList();
    }
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
