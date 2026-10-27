using System.Text.Json.Nodes;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Billing.Accounts;
using IMed.Api.Domain.Billing.Catalog;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.Billing;

/// <summary>Tipo de consulta propuesto y por qué.</summary>
public sealed record ConsultationTypeSuggestionDto(string Type, string Display, string Reason, int WindowDays);

/// <summary>
/// Propone el tipo de consulta (P1): <b>control</b> si el paciente tuvo una consulta de la misma
/// especialidad dentro de la ventana de la organización; si no, <b>primera vez</b>. Emergencia no
/// se propone nunca: la decide una persona.
///
/// <para>Se calcula con los cargos y no con los encuentros de FHIR: el cargo ya sabe la
/// especialidad (su cobrable) y la fecha en que se atendió, y una consulta que se canceló y se
/// reversó no tiene que contar como antecedente.</para>
/// </summary>
public sealed class SuggestConsultationTypeHandler(
    IBillingAccountRepository accounts,
    IBillingSettingsRepository settings,
    TimeProvider timeProvider)
{
    public async Task<ConsultationTypeSuggestionDto> HandleAsync(
        Guid orgId, string patientFhirId, string? specialtyCode, string? excludeEncounterFhirId = null,
        CancellationToken ct = default)
    {
        var especialidad = string.IsNullOrWhiteSpace(specialtyCode)
            ? ConsultationTypeCodes.GeneralPracticeSpecialty
            : specialtyCode.Trim();
        var ventana = (await settings.GetAsync(orgId, ct)).FollowUpWindowDays;

        var esControl = ventana > 0 && await accounts.HasConsultationSinceAsync(
            orgId, patientFhirId, especialidad, timeProvider.GetUtcNow().AddDays(-ventana),
            excludeEncounterFhirId, ct);

        var tipo = esControl ? ConsultationType.FollowUp : ConsultationType.FirstVisit;
        return new ConsultationTypeSuggestionDto(
            ConsultationTypeCodes.ToCode(tipo),
            ConsultationTypeCodes.Display(tipo),
            esControl
                ? $"Tuvo una consulta de esta especialidad en los últimos {ventana} días."
                : $"No tuvo consultas de esta especialidad en los últimos {ventana} días.",
            ventana);
    }
}

/// <summary>Qué hizo la reconciliación de una consulta. Sirve a las pruebas y a la bitácora.</summary>
public enum ConsultationChargeOutcome
{
    /// <summary>El encuentro no cobra en este estado (planificado, en sala de espera).</summary>
    NotBillable,
    Captured,
    AlreadyCharged,
    /// <summary>Cambió el tipo de consulta: se reversó el cargo anterior y se capturó el nuevo.</summary>
    Replaced,
    Reversed,
    /// <summary>El cargo ya estaba facturado: no se toca (hace falta nota de crédito, P4).</summary>
    InvoicedUntouched,
    /// <summary>No se pudo cobrar (sin régimen fiscal, sin sede, cuenta cerrada…). Va al log.</summary>
    Skipped,
}

/// <summary>
/// Mantiene el cargo de una consulta de acuerdo con su encuentro (plan de salida a producción,
/// D2, D3 y A4).
///
/// <para><b>Reconcilia, no reacciona.</b> No mira qué cambió: lee el encuentro tal como está y deja
/// el cargo como debe quedar. Por eso se puede llamar cuantas veces haga falta —cada escritura del
/// encuentro, cada reintento del outbox— y el resultado es el mismo.</para>
///
/// <list type="bullet">
///   <item>En curso, en pausa o terminado: la consulta se cobra. Cuenta del encuentro y un cargo
///   del servicio especialidad + tipo, en la sede del encuentro.</item>
///   <item>Cancelado, interrumpido o anulado: el cargo se reversa si no está facturado.</item>
///   <item>Cambió el tipo y el cargo no está facturado: se reversa y se captura el nuevo.</item>
/// </list>
///
/// <para><b>Nunca detiene la consulta.</b> Lo llama el outbox después de que HAPI aceptó la
/// escritura; si no puede cobrar, deja constancia en el log y el reporte de consultas sin cargo.</para>
/// </summary>
public sealed class EncounterChargeReconciler(
    IHapiFhirClient hapi,
    IBillableItemProvisioner provisioner,
    IEncounterAccountResolver accountResolver,
    IBillingAccountRepository accounts,
    CaptureChargeHandler capture,
    SuggestConsultationTypeHandler suggester,
    ILogger<EncounterChargeReconciler> logger)
{
    private static readonly HashSet<string> Cobrables = ["in-progress", "on-hold", "completed", "discharged"];
    private static readonly HashSet<string> Anulados = ["cancelled", "entered-in-error", "discontinued"];

    public async Task<ConsultationChargeOutcome> ReconcileAsync(
        Guid orgId, string encounterFhirId, CancellationToken ct = default)
    {
        var particion = $"org-{orgId}";
        var encuentro = await hapi.ReadResourceAsync("Encounter", encounterFhirId, particion, ct);
        if (encuentro is null)
        {
            logger.LogWarning("Cobro de consulta: el encuentro {Encounter} no existe.", encounterFhirId);
            return ConsultationChargeOutcome.Skipped;
        }

        var estado = encuentro["status"]?.GetValue<string>() ?? string.Empty;
        var vivos = await accounts.ListLiveChargesBySourceForUpdateAsync(
            orgId, ChargeSourceKind.Encounter, encounterFhirId, ct);

        if (Anulados.Contains(estado))
        {
            return await ReversarAsync(vivos, "Consulta cancelada", ct);
        }
        if (!Cobrables.Contains(estado))
        {
            return ConsultationChargeOutcome.NotBillable;
        }

        var paciente = RefId(encuentro["subject"]?["reference"], "Patient");
        var sede = SedeDe(encuentro);
        if (paciente is null || sede is null)
        {
            logger.LogWarning(
                "Cobro de consulta: el encuentro {Encounter} no tiene paciente o sede; queda sin cargo.",
                encounterFhirId);
            return ConsultationChargeOutcome.Skipped;
        }

        var medico = encuentro["participant"]?.AsArray()
            .Select(p => RefId(p?["actor"]?["reference"], "Practitioner"))
            .FirstOrDefault(id => id is not null);
        var especialidad = EspecialidadDe(encuentro)
            ?? await EspecialidadDelMedicoAsync(orgId, medico, ct)
            ?? ConsultationTypeCodes.GeneralPracticeSpecialty;

        var tipo = TipoDe(encuentro) ?? ConsultationTypeCodes.FromCode(
            (await suggester.HandleAsync(orgId, paciente, especialidad, encounterFhirId, ct)).Type)!.Value;

        var items = await provisioner.EnsureForSpecialtyAsync(orgId, especialidad, ct);
        var item = items.FirstOrDefault(i => i.ConsultationType == tipo.ToString());
        if (item is null)
        {
            logger.LogWarning(
                "Cobro de consulta: la organización {Org} no tiene régimen fiscal; el encuentro {Encounter} queda sin cargo.",
                orgId, encounterFhirId);
            return ConsultationChargeOutcome.Skipped;
        }

        var actual = vivos.Count > 0 ? vivos[0] : null;
        if (actual is not null && actual.BillableItemId == item.Id)
        {
            return ConsultationChargeOutcome.AlreadyCharged;
        }
        if (vivos.Any(c => c.Status == ChargeStatus.Invoiced))
        {
            logger.LogWarning(
                "Cobro de consulta: el encuentro {Encounter} cambió de tipo con el cargo ya facturado; se mantiene (nota de crédito).",
                encounterFhirId);
            return ConsultationChargeOutcome.InvoicedUntouched;
        }

        var reemplazo = vivos.Count > 0;
        if (reemplazo)
        {
            await ReversarAsync(vivos, $"Cambió el tipo de consulta a {ConsultationTypeCodes.Display(tipo)}", ct);
        }

        var nombre = await NombreDelPacienteAsync(paciente, particion, ct);
        var cuenta = await accountResolver.GetOrOpenForEncounterAsync(orgId, encounterFhirId, paciente, nombre, ct);
        if (cuenta is null)
        {
            logger.LogWarning(
                "Cobro de consulta: la cuenta del encuentro {Encounter} ya no acepta cargos.", encounterFhirId);
            return ConsultationChargeOutcome.Skipped;
        }

        var r = await capture.HandleAsync(new CaptureChargeCommand(
            orgId, cuenta.Value, item.Id, ChargeSourceKind.Encounter, encounterFhirId,
            PerformedAt: InicioDe(encuentro),
            PerformerPractitionerId: medico,
            SourceFhirRef: $"Encounter/{encounterFhirId}",
            LocationId: sede), ct);

        if (!r.Succeeded)
        {
            logger.LogWarning("Cobro de consulta {Encounter} rechazado: {Motivo}", encounterFhirId, r.Rejected);
            return ConsultationChargeOutcome.Skipped;
        }
        return r.AlreadyCharged ? ConsultationChargeOutcome.AlreadyCharged
            : reemplazo ? ConsultationChargeOutcome.Replaced
            : ConsultationChargeOutcome.Captured;
    }

    private async Task<ConsultationChargeOutcome> ReversarAsync(
        IReadOnlyList<BillingCharge> vivos, string motivo, CancellationToken ct)
    {
        if (vivos.Count == 0)
        {
            return ConsultationChargeOutcome.NotBillable;
        }
        if (vivos.Any(c => c.Status == ChargeStatus.Invoiced))
        {
            // P4: lo facturado se revierte con nota de crédito, que es una decisión de una persona.
            return ConsultationChargeOutcome.InvoicedUntouched;
        }
        foreach (var c in vivos)
        {
            c.Reverse(motivo);
            await accounts.UpdateChargeAsync(c, ct);
        }
        return ConsultationChargeOutcome.Reversed;
    }

    private async Task<string?> EspecialidadDelMedicoAsync(Guid orgId, string? practitionerId, CancellationToken ct)
    {
        if (practitionerId is null)
        {
            return null;
        }
        var particion = $"org-{orgId}";
        var roles = await hapi.SearchResourcesAsync(
            "PractitionerRole", $"practitioner=Practitioner/{practitionerId}&active=true", particion, ct);
        return roles
            .Select(r => r["specialty"]?.AsArray().FirstOrDefault()?["coding"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>())
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
    }

    private async Task<string?> NombreDelPacienteAsync(string patientId, string particion, CancellationToken ct)
    {
        try
        {
            var p = await hapi.GetPatientAsync(patientId, particion, ct);
            return p?.DisplayName;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // El nombre solo sirve para la parte nueva; sin él queda "Paciente" y se corrige al facturar.
            logger.LogDebug(ex, "No se pudo leer el nombre del paciente {Patient}.", patientId);
            return null;
        }
    }

    /// <summary>Especialidad declarada en el encuentro (<c>serviceType</c>, R5 CodeableReference).</summary>
    internal static string? EspecialidadDe(JsonObject e) =>
        e["serviceType"]?.AsArray()
            .Select(s => s?["concept"]?["coding"]?.AsArray().FirstOrDefault()?["code"]?.GetValue<string>())
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    /// <summary>Tipo de consulta declarado en el encuentro (<c>type</c> con el sistema de IMed).</summary>
    internal static ConsultationType? TipoDe(JsonObject e) =>
        e["type"]?.AsArray()
            .SelectMany(t => t?["coding"]?.AsArray() ?? [])
            .Where(c => c?["system"]?.GetValue<string>() == ConsultationTypeCodes.System)
            .Select(c => ConsultationTypeCodes.FromCode(c?["code"]?.GetValue<string>()))
            .FirstOrDefault(t => t is not null);

    /// <summary>La sede del encuentro: <c>Location/sede-{guid:N}</c> (FAC-9).</summary>
    internal static Guid? SedeDe(JsonObject e)
    {
        var referencia = e["location"]?.AsArray()
            .Select(l => l?["location"]?["reference"]?.GetValue<string>())
            .FirstOrDefault(r => r is not null && r.StartsWith("Location/sede-", StringComparison.Ordinal));
        return referencia is not null && Guid.TryParseExact(referencia["Location/sede-".Length..], "N", out var id)
            ? id : null;
    }

    private static DateTimeOffset? InicioDe(JsonObject e) =>
        DateTimeOffset.TryParse(e["actualPeriod"]?["start"]?.GetValue<string>(), out var t) ? t : null;

    private static string? RefId(JsonNode? reference, string type)
    {
        var r = reference?.GetValue<string>();
        var prefijo = type + "/";
        return r is not null && r.StartsWith(prefijo, StringComparison.Ordinal) ? r[prefijo.Length..] : null;
    }
}
