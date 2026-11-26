using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabResults;

/// <summary>
/// Una línea de la captura reabierta. Con <paramref name="ObservationId"/> es un resultado ya
/// guardado; sin él, un analito que se dejó en blanco en la captura original y ahora se carga.
/// </summary>
public sealed record AmendResultLine(
    string? ObservationId,
    Guid? LabTestId,
    string? Loinc,
    string CodeText,
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit);

public sealed record AmendResultsCommand(
    Guid OrganizationId,
    Guid UserId,
    string ReportId,
    string Reason,
    IReadOnlyList<AmendResultLine> Results,
    string? Conclusion);

public sealed record AmendResultsResult(string ReportId, int Changed, int Added, bool ConclusionChanged);

/// <summary>
/// Reabre la captura completa de un informe <b>no liberado</b> (observación de staging: los
/// técnicos piden poder modificar los resultados mientras no se liberen, y la corrección de a un
/// valor —solo numérica— no alcanzaba cuando se equivocaban en varios).
///
/// <list type="bullet">
///   <item><b>Solo lo que cambió.</b> Cada resultado guardado cuyo valor difiere se reescribe
///   sobre la misma <c>Observation</c> (HAPI versiona: el valor anterior queda en
///   <c>_history</c>) con el flag recalculado contra los rangos del paciente. Lo que no cambió no
///   se toca.</item>
///   <item><b>Lo que faltaba se agrega</b> al informe (y al grouper del panel).</item>
///   <item><b>Un resultado cargado no se puede vaciar</b>: quitarlo del informe es borrar un dato
///   clínico, y eso no se resuelve desde la captura.</item>
///   <item><b>Un motivo para todo el cambio</b>, que queda en la bitácora de auditoría con el
///   antes y el después de cada valor. La corrección de a un valor pedía motivo pero no lo
///   guardaba en ningún lado.</item>
///   <item><b>Liberado = bloqueado</b>, igual que la corrección: el resultado ya salió del
///   laboratorio.</item>
/// </list>
///
/// <para>Todo se valida antes de escribir en HAPI: un panel que falle a mitad de camino dejaría
/// cambios a medias.</para>
/// </summary>
public sealed class AmendResultsHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    IAppAuditLog auditLog,
    TimeProvider timeProvider,
    IClinicClock clock)
{
    /// <summary>Mismo status que la corrección de un valor: el consumidor sabe que reemplaza a otro.</summary>
    private const string CorrectedStatus = "corrected";

    public async Task<AmendResultsResult?> HandleAsync(AmendResultsCommand cmd, CancellationToken ct = default)
    {
        var reason = cmd.Reason?.Trim() ?? string.Empty;
        if (reason.Length == 0)
        {
            throw new ArgumentException("La corrección requiere un motivo.", nameof(cmd));
        }

        var partition = $"org-{cmd.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(cmd.ReportId, partition, ct);
        if (report is null)
        {
            return null;
        }
        if (string.Equals(report.Status, "final", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "El informe ya fue liberado y no se puede modificar desde la captura. Requiere una enmienda del informe.");
        }

        // ── 1. Leer y validar todo antes de escribir ────────────────────────
        var enInforme = report.ResultObservationIds.ToHashSet(StringComparer.Ordinal);
        var guardadas = new Dictionary<string, ObservationSnapshot>(StringComparer.Ordinal);
        foreach (var id in report.ResultObservationIds)
        {
            var obs = await hapi.GetObservationAsync(id, partition, ct);
            if (obs is not null)
            {
                guardadas[id] = obs;
            }
        }
        // El grouper del panel no tiene valor: es el encabezado.
        var grouper = guardadas.Values.FirstOrDefault(o => o.ValueNumeric is null && string.IsNullOrWhiteSpace(o.ValueString));
        var referencia = guardadas.Values.FirstOrDefault(o => o.ValueNumeric is not null || !string.IsNullOrWhiteSpace(o.ValueString));

        var cambios = new List<(AmendResultLine Line, ObservationSnapshot Before)>();
        var nuevas = new List<AmendResultLine>();
        foreach (var line in cmd.Results)
        {
            var tieneValor = line.ValueNumeric is not null || !string.IsNullOrWhiteSpace(line.ValueString);
            if (line.ValueNumeric is not null && !LabUnits.IsAllowed(line.Unit))
            {
                throw new ArgumentException(
                    $"La unidad '{line.Unit}' de «{line.CodeText}» no está en la lista de unidades admitidas.", nameof(cmd));
            }

            if (string.IsNullOrWhiteSpace(line.ObservationId))
            {
                if (tieneValor)
                {
                    nuevas.Add(line);
                }
                continue;
            }

            if (!enInforme.Contains(line.ObservationId) || !guardadas.TryGetValue(line.ObservationId, out var antes))
            {
                throw new ArgumentException($"El resultado «{line.CodeText}» no pertenece a este informe.", nameof(cmd));
            }
            if (!tieneValor)
            {
                throw new ArgumentException(
                    $"«{line.CodeText}» ya tenía resultado y no se puede dejar vacío. Corregí el valor en lugar de borrarlo.",
                    nameof(cmd));
            }
            if (MismoValor(antes, line))
            {
                continue;
            }
            cambios.Add((line, antes));
        }

        var conclusionNueva = cmd.Conclusion?.Trim() ?? string.Empty;
        var conclusionCambio = !string.Equals(conclusionNueva, report.Conclusion?.Trim() ?? string.Empty, StringComparison.Ordinal);

        if (cambios.Count == 0 && nuevas.Count == 0 && !conclusionCambio)
        {
            throw new ArgumentException("No hay cambios que guardar.", nameof(cmd));
        }

        // ── 2. Escribir ─────────────────────────────────────────────────────
        var patient = await hapi.GetPatientAsync(report.PatientFhirId, partition, ct);
        var sex = ResultInterpreter.ParseSex(patient?.Gender);
        var ageYears = ResultInterpreter.AgeYearsFromBirthDate(patient?.BirthDate, clock.Today());
        var now = timeProvider.GetUtcNow().ToString("o");

        var auditoria = new List<object>();
        foreach (var (line, antes) in cambios)
        {
            var interp = await InterpretAsync(cmd.OrganizationId, line, antes.LoincCode, sex, ageYears, ct);
            var unidad = line.ValueNumeric is null
                ? null
                : LabUnits.Find(line.Unit)?.Code ?? antes.ValueUnitCode ?? antes.ValueUnit;

            await hapi.UpdateObservationAsync(antes.Id, new UpsertObservationRequest(
                PartitionName: partition,
                PatientFhirId: antes.PatientFhirId,
                Status: CorrectedStatus,
                LoincCode: antes.LoincCode,
                CodeText: antes.CodeText ?? line.CodeText,
                ValueNumeric: line.ValueNumeric,
                ValueUnit: unidad,
                ValueString: line.ValueNumeric is null ? line.ValueString?.Trim() : null,
                InterpretationCode: interp.Flag,
                ReferenceLow: interp.Low,
                ReferenceHigh: interp.High,
                // El PUT reemplaza el recurso: los vínculos vuelven tal cual estaban.
                SpecimenFhirId: antes.SpecimenFhirId,
                EffectiveDateTime: now,
                PerformerReference: antes.PerformerReference,
                ServiceRequestFhirId: antes.ServiceRequestFhirId,
                AppliesToSex: interp.AppliesToSex,
                AppliesToAgeMin: interp.AgeMin,
                AppliesToAgeMax: interp.AgeMax), ct);

            auditoria.Add(new
            {
                observationId = antes.Id,
                codeText = antes.CodeText ?? line.CodeText,
                before = ValorLegible(antes.ValueNumeric, antes.ValueString),
                after = ValorLegible(line.ValueNumeric, line.ValueString),
            });
        }

        var agregadas = new List<string>();
        foreach (var line in nuevas)
        {
            var interp = await InterpretAsync(cmd.OrganizationId, line, line.Loinc, sex, ageYears, ct);
            var id = await hapi.CreateObservationAsync(new UpsertObservationRequest(
                PartitionName: partition,
                PatientFhirId: report.PatientFhirId,
                Status: "preliminary",
                LoincCode: line.Loinc,
                CodeText: line.CodeText,
                ValueNumeric: line.ValueNumeric,
                ValueUnit: line.ValueNumeric is null ? null : LabUnits.Find(line.Unit)?.Code,
                ValueString: line.ValueNumeric is null ? line.ValueString?.Trim() : null,
                InterpretationCode: interp.Flag,
                ReferenceLow: interp.Low,
                ReferenceHigh: interp.High,
                SpecimenFhirId: referencia?.SpecimenFhirId ?? report.SpecimenFhirId,
                EffectiveDateTime: now,
                PerformerReference: referencia?.PerformerReference,
                ServiceRequestFhirId: referencia?.ServiceRequestFhirId ?? report.ServiceRequestFhirId,
                AppliesToSex: interp.AppliesToSex,
                AppliesToAgeMin: interp.AgeMin,
                AppliesToAgeMax: interp.AgeMax), ct);
            agregadas.Add(id);
            auditoria.Add(new { observationId = id, codeText = line.CodeText, before = (string?)null, after = ValorLegible(line.ValueNumeric, line.ValueString) });
        }
        if (agregadas.Count > 0)
        {
            var referencias = agregadas.Select(id => $"Observation/{id}").ToList();
            await hapi.AppendReferencesAsync("DiagnosticReport", report.Id, "result", referencias, partition, ct);
            if (grouper is not null)
            {
                await hapi.AppendReferencesAsync("Observation", grouper.Id, "hasMember", referencias, partition, ct);
            }
        }

        if (conclusionCambio)
        {
            await hapi.PatchDiagnosticReportConclusionAsync(report.Id, conclusionNueva, partition, ct);
        }

        // Corregir es también la respuesta a una devolución del validador: se cierra y el informe
        // vuelve a la cola de validación (mismo comportamiento que la corrección de un valor).
        var serviceRequestId = report.ServiceRequestFhirId ?? referencia?.ServiceRequestFhirId;
        if (!string.IsNullOrWhiteSpace(serviceRequestId))
        {
            var intake = await ops.GetIntakeByServiceRequestAsync(cmd.OrganizationId, serviceRequestId!, ct);
            if (!string.IsNullOrWhiteSpace(intake?.ReportFhirId))
            {
                await ops.ResolveValidationRejectionsAsync(cmd.OrganizationId, intake!.ReportFhirId!, ct);
            }
        }

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "lab",
            action: "results_amended",
            severity: AuditSeverity.Info,
            actorUserId: cmd.UserId,
            organizationId: cmd.OrganizationId,
            target: $"DiagnosticReport:{report.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                reason,
                changes = auditoria,
                conclusion = conclusionCambio ? new { before = report.Conclusion, after = conclusionNueva } : null,
            }),
            timeProvider: timeProvider), ct);

        return new AmendResultsResult(report.Id, cambios.Count, agregadas.Count, conclusionCambio);
    }

    private static bool MismoValor(ObservationSnapshot antes, AmendResultLine line)
    {
        if (line.ValueNumeric is { } n)
        {
            return antes.ValueNumeric is { } v && v == n;
        }
        return antes.ValueNumeric is null
            && string.Equals(antes.ValueString?.Trim(), line.ValueString?.Trim(), StringComparison.Ordinal);
    }

    private static string? ValorLegible(decimal? numeric, string? text) =>
        numeric?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? text?.Trim();

    private async Task<Interpretation> InterpretAsync(
        Guid orgId, AmendResultLine line, string? loinc, ReferenceSex sex, int? ageYears, CancellationToken ct)
    {
        if (line.ValueNumeric is not { } value)
        {
            return Interpretation.None;
        }
        LabTestDefinition? test = null;
        if (line.LabTestId is { } id)
        {
            var porId = await catalog.GetTestByIdAsync(id, ct);
            test = porId is not null && porId.OrganizationId == orgId ? porId : null;
        }
        if (test is null && !string.IsNullOrWhiteSpace(loinc))
        {
            test = await catalog.GetTestByLoincAsync(orgId, loinc!, ct);
        }
        if (test is null)
        {
            return Interpretation.None;
        }
        var ranges = await catalog.ListRangesByTestAsync(orgId, test.Id, ct);
        var interp = ResultInterpreter.Interpret(ranges, sex, ageYears, value);
        return new Interpretation(
            interp.Code, interp.Low, interp.High, interp.AppliesToSex?.ToString(), interp.AppliesToAgeMin, interp.AppliesToAgeMax);
    }

    private sealed record Interpretation(
        string? Flag, decimal? Low, decimal? High, string? AppliesToSex, int? AgeMin, int? AgeMax)
    {
        public static Interpretation None { get; } = new(null, null, null, null, null, null);
    }
}
