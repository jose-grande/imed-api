using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing;
using IMed.Api.Domain.Billing.Accounts;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Encuentra o abre la cuenta de un acto (plan de salida a producción, D2 y F0.4).
///
/// <para><b>Una cuenta por consulta.</b> La consulta, los exámenes que se ordenan en ella y los
/// medicamentos que se despachan de su receta van a la misma cuenta. Lo que no tiene consulta —una
/// orden de laboratorio externa, una venta libre de farmacia— abre su propia cuenta.</para>
///
/// <para><b>Es idempotente por acto.</b> Lo llaman caminos que se reintentan (el outbox, una orden
/// reenviada) y dos llamadas a la vez: el índice único de la cuenta decide y el perdedor vuelve a
/// leer la que ganó.</para>
/// </summary>
public interface IEncounterAccountResolver
{
    /// <summary>
    /// La cuenta de episodio de una consulta, abriéndola si no existe. <c>null</c> si la cuenta
    /// existe pero ya no acepta cargos (facturada, cerrada o anulada).
    /// </summary>
    Task<Guid?> GetOrOpenForEncounterAsync(
        Guid organizationId, string encounterFhirId, string patientFhirId, string? patientName,
        CancellationToken ct = default);

    /// <summary>
    /// La cuenta propia de un acto sin consulta (orden de laboratorio externa, venta libre),
    /// abriéndola si no existe. <c>null</c> si existe y ya no acepta cargos.
    /// </summary>
    Task<Guid?> GetOrOpenStandaloneAsync(
        Guid organizationId, BillingAccountOrigin origin, string originRef,
        string patientFhirId, string? patientName, CancellationToken ct = default);
}

public sealed class EncounterAccountResolver(
    IBillingAccountRepository accounts,
    IPartyRepository parties) : IEncounterAccountResolver
{
    public async Task<Guid?> GetOrOpenForEncounterAsync(
        Guid organizationId, string encounterFhirId, string patientFhirId, string? patientName,
        CancellationToken ct = default)
    {
        Requerido(encounterFhirId, nameof(encounterFhirId));
        Requerido(patientFhirId, nameof(patientFhirId));
        var encuentro = encounterFhirId.Trim();

        return await ObtenerOAbrirAsync(
            () => accounts.FindByEncounterAsync(organizationId, encuentro, ct),
            partyId => BillingAccount.Open(
                organizationId, partyId, BillingAccountKind.Episode,
                patientFhirId, encounterFhirId: encuentro),
            organizationId, patientFhirId, patientName, ct);
    }

    public async Task<Guid?> GetOrOpenStandaloneAsync(
        Guid organizationId, BillingAccountOrigin origin, string originRef,
        string patientFhirId, string? patientName, CancellationToken ct = default)
    {
        if (origin is BillingAccountOrigin.Manual or BillingAccountOrigin.Encounter)
        {
            throw new ArgumentException(
                "Una cuenta suelta nace de una orden o de una venta; la de una consulta se pide por el encuentro.",
                nameof(origin));
        }
        Requerido(originRef, nameof(originRef));
        Requerido(patientFhirId, nameof(patientFhirId));
        var referencia = originRef.Trim();

        return await ObtenerOAbrirAsync(
            () => accounts.FindByOriginAsync(organizationId, origin, referencia, ct),
            partyId => BillingAccount.Open(
                organizationId, partyId, BillingAccountKind.Episode,
                patientFhirId, origin: origin, originRef: referencia),
            organizationId, patientFhirId, patientName, ct);
    }

    private async Task<Guid?> ObtenerOAbrirAsync(
        Func<Task<BillingAccount?>> buscar,
        Func<Guid, BillingAccount> abrir,
        Guid organizationId, string patientFhirId, string? patientName, CancellationToken ct)
    {
        if (await buscar() is { } existente)
        {
            return existente.AcceptsCharges ? existente.Id : null;
        }

        var partyId = await ParteDelPacienteAsync(organizationId, patientFhirId, patientName, ct);
        var nueva = abrir(partyId);
        if (await accounts.TryAddAccountAsync(nueva, ct))
        {
            return nueva.Id;
        }

        // Otro camino la abrió entre la lectura y la escritura.
        var ganadora = await buscar();
        return ganadora is { AcceptsCharges: true } ? ganadora.Id : null;
    }

    /// <summary>
    /// La parte del paciente, creándola si no existe: obligar a darla de alta aparte convierte
    /// "cobrar una consulta" en dos trámites, y el segundo se olvida.
    /// </summary>
    private async Task<Guid> ParteDelPacienteAsync(
        Guid organizationId, string patientFhirId, string? patientName, CancellationToken ct)
    {
        var paciente = patientFhirId.Trim();
        if (await parties.FindByPatientAsync(organizationId, paciente, ct) is { } existente)
        {
            return existente.Id;
        }

        var nueva = Party.Create(
            organizationId, PartyKind.Patient,
            legalName: string.IsNullOrWhiteSpace(patientName) ? "Paciente" : patientName.Trim(),
            patientFhirId: paciente);
        if (await parties.TryAddAsync(nueva, ct))
        {
            return nueva.Id;
        }
        return (await parties.FindByPatientAsync(organizationId, paciente, ct))?.Id
            ?? throw new InvalidOperationException("No se pudo crear ni encontrar la parte del paciente.");
    }

    private static void Requerido(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} es obligatorio.", name);
        }
    }
}
