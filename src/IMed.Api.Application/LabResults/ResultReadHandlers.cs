using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.LabResults;

public sealed record LabObservationDto(
    string Id,
    string? LoincCode,
    string? CodeText,
    decimal? ValueNumeric,
    string? ValueUnit,
    string? ValueString,
    string? FlagCode,
    decimal? ReferenceLow,
    decimal? ReferenceHigh,
    string Status,
    string? EffectiveDateTime,
    /// <summary>Último valor previo del mismo analito en este paciente. Null si no hay histórico.</summary>
    decimal? PreviousValue = null,
    string? PreviousDate = null,
    /// <summary>
    /// Diferencia contra el valor previo. Es la mitad del delta check: el motor lo evaluó al
    /// guardar, pero el validador no lo veía — tenía que deducirlo de la lista de historial.
    /// </summary>
    decimal? Delta = null);

/// <summary>Una regla que disparó sobre una observación del informe.</summary>
public sealed record ReportRuleHitDto(
    string ObservationFhirId,
    string RuleCode,
    string RuleName,
    string Verdict,
    string Severity,
    string? Message);

public sealed record LabReportSummaryDto(
    string ReportId,
    string Status,
    string? LoincCode,
    string? CodeText,
    string? Issued,
    string? Conclusion);

public sealed record LabReportDetailDto(
    string ReportId,
    string Status,
    string PatientFhirId,
    string? LoincCode,
    string? CodeText,
    string? Issued,
    string? Conclusion,
    string? PerformerDisplay,
    string? ServiceRequestId,
    IReadOnlyList<LabObservationDto> Observations,
    /// <summary>
    /// Nombre del paciente. La pantalla de validación mostraba el id FHIR crudo, que no le dice
    /// nada a nadie — mismo defecto que ya se había corregido en la worklist.
    /// </summary>
    string? PatientName = null,
    /// <summary>
    /// Reglas que dispararon sobre este informe: el "por qué está acá". La auto-validación ya
    /// libera sola lo normal, así que lo que llega a validar es justo lo que el motor decidió no
    /// firmar; sin esto la pantalla mostraba el valor pero no el motivo de la desconfianza.
    /// </summary>
    IReadOnlyList<ReportRuleHitDto>? RuleHits = null,
    /// <summary>Devolución abierta al técnico, si la hay (motivo + nota del validador).</summary>
    string? OpenRejectionReason = null,
    string? OpenRejectionNote = null);

// ---------------------------------------------------------------------------

public sealed record GetResultsByPatientQuery(Guid OrganizationId, string PatientFhirId);

public sealed class GetResultsByPatientHandler(IHapiFhirClient hapi)
{
    public async Task<IReadOnlyList<LabReportSummaryDto>> HandleAsync(GetResultsByPatientQuery q, CancellationToken ct = default)
    {
        var partition = $"org-{q.OrganizationId}";
        var reports = await hapi.SearchDiagnosticReportsByPatientAsync(q.PatientFhirId, partition, ct);
        return reports
            .Select(r => new LabReportSummaryDto(r.Id, r.Status, r.LoincCode, r.CodeText, r.Issued, r.Conclusion))
            .ToList();
    }
}

public sealed record GetResultDetailQuery(Guid OrganizationId, string DiagnosticReportId);

public sealed class GetResultDetailHandler(
    IHapiFhirClient hapi,
    ILabRuleRepository rules,
    ILabOperationsRepository ops)
{
    /// <summary>Cuántos puntos previos se piden para calcular el delta (basta el último).</summary>
    private const int PreviousPoints = 1;

    public async Task<LabReportDetailDto?> HandleAsync(GetResultDetailQuery q, CancellationToken ct = default)
    {
        var partition = $"org-{q.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(q.DiagnosticReportId, partition, ct);
        if (report is null)
        {
            return null;
        }

        var observations = new List<LabObservationDto>();
        var observationIds = new List<string>();
        foreach (var obsId in report.ResultObservationIds)
        {
            var o = await hapi.GetObservationAsync(obsId, partition, ct);
            if (o is null)
            {
                continue;
            }
            observationIds.Add(o.Id);

            // Valor previo y delta por analito. El motor ya los evaluó al guardar, pero el
            // validador no los veía: tenía que inferirlos de una lista de historial suelta.
            decimal? previousValue = null;
            string? previousDate = null;
            if (!string.IsNullOrWhiteSpace(o.LoincCode))
            {
                var history = await hapi.GetObservationHistoryAsync(
                    report.PatientFhirId, o.LoincCode!, partition, PreviousPoints + 1, ct);
                // El primer punto puede ser esta misma observación: se busca el primero distinto.
                var prior = history.FirstOrDefault(p => p.Value != o.ValueNumeric);
                if (prior is not null)
                {
                    previousValue = prior.Value;
                    previousDate = prior.Date;
                }
            }

            observations.Add(new LabObservationDto(
                o.Id, o.LoincCode, o.CodeText, o.ValueNumeric, o.ValueUnit, o.ValueString,
                o.FlagCode, o.ReferenceLow, o.ReferenceHigh, o.Status, o.EffectiveDateTime,
                PreviousValue: previousValue,
                PreviousDate: previousDate,
                Delta: previousValue is { } pv && o.ValueNumeric is { } cv ? cv - pv : null));
        }

        var hits = await rules.ListFiredExecutionsAsync(q.OrganizationId, observationIds, ct);
        var open = (await ops.ListOpenValidationRejectionsAsync(q.OrganizationId, ct))
            .FirstOrDefault(r => string.Equals(r.ReportFhirId, report.Id, StringComparison.Ordinal));
        string? openReasonName = null;
        if (open is not null)
        {
            var reasons = await ops.ListValidationRejectionReasonsAsync(q.OrganizationId, ct);
            openReasonName = reasons.FirstOrDefault(r => r.Id == open.ReasonId)?.Name;
        }

        var patient = await hapi.GetPatientAsync(report.PatientFhirId, partition, ct);

        return new LabReportDetailDto(
            report.Id, report.Status, report.PatientFhirId, report.LoincCode, report.CodeText,
            report.Issued, report.Conclusion, report.PerformerDisplay, report.ServiceRequestFhirId, observations,
            PatientName: patient?.DisplayName,
            RuleHits: hits.Select(h => new ReportRuleHitDto(
                h.ObservationFhirId, h.RuleCode, h.RuleName,
                h.Verdict.ToString(), h.Severity.ToString(), h.Message)).ToList(),
            OpenRejectionReason: openReasonName,
            OpenRejectionNote: open?.Note);
    }
}
