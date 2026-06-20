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
    string? EffectiveDateTime);

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
    IReadOnlyList<LabObservationDto> Observations);

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

public sealed class GetResultDetailHandler(IHapiFhirClient hapi)
{
    public async Task<LabReportDetailDto?> HandleAsync(GetResultDetailQuery q, CancellationToken ct = default)
    {
        var partition = $"org-{q.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(q.DiagnosticReportId, partition, ct);
        if (report is null)
        {
            return null;
        }

        var observations = new List<LabObservationDto>();
        foreach (var obsId in report.ResultObservationIds)
        {
            var o = await hapi.GetObservationAsync(obsId, partition, ct);
            if (o is not null)
            {
                observations.Add(new LabObservationDto(
                    o.Id, o.LoincCode, o.CodeText, o.ValueNumeric, o.ValueUnit, o.ValueString,
                    o.FlagCode, o.ReferenceLow, o.ReferenceHigh, o.Status, o.EffectiveDateTime));
            }
        }

        return new LabReportDetailDto(
            report.Id, report.Status, report.PatientFhirId, report.LoincCode, report.CodeText,
            report.Issued, report.Conclusion, report.PerformerDisplay, report.ServiceRequestFhirId, observations);
    }
}
