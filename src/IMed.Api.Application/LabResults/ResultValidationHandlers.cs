using IMed.Api.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabResults;

public sealed record HistoryPointDto(decimal Value, string? Date, string? FlagCode);
public sealed record ReleaseReportResult(string ReportId, string Status);

// --------------------------- Pendientes ---------------------------

public sealed record GetPendingValidationQuery(Guid OrganizationId);

public sealed class GetPendingValidationHandler(IHapiFhirClient hapi)
{
    public async Task<IReadOnlyList<LabReportSummaryDto>> HandleAsync(GetPendingValidationQuery q, CancellationToken ct = default)
    {
        var reports = await hapi.SearchPreliminaryReportsAsync($"org-{q.OrganizationId}", ct);
        return reports
            .Select(r => new LabReportSummaryDto(r.Id, r.Status, r.LoincCode, r.CodeText, r.Issued, r.Conclusion))
            .ToList();
    }
}

// --------------------------- Historial/tendencia ---------------------------

public sealed record GetReportHistoryQuery(Guid OrganizationId, string ReportId);

public sealed class GetReportHistoryHandler(IHapiFhirClient hapi)
{
    public async Task<IReadOnlyList<HistoryPointDto>> HandleAsync(GetReportHistoryQuery q, CancellationToken ct = default)
    {
        var partition = $"org-{q.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(q.ReportId, partition, ct);
        if (report is null || string.IsNullOrWhiteSpace(report.LoincCode))
        {
            return [];
        }
        var points = await hapi.GetObservationHistoryAsync(report.PatientFhirId, report.LoincCode!, partition, 10, ct);
        return points.Select(p => new HistoryPointDto(p.Value, p.Date, p.FlagCode)).ToList();
    }
}

// --------------------------- Liberación ---------------------------

public sealed record ReleaseReportCommand(Guid OrganizationId, Guid UserId, string ReportId);

public sealed class ReleaseReportHandler(
    IHapiFhirClient hapi,
    ILabOperationsRepository ops,
    IUserPractitionerAssignmentRepository assignments,
    TimeProvider timeProvider,
    ILogger<ReleaseReportHandler> logger)
{
    public async Task<ReleaseReportResult> HandleAsync(ReleaseReportCommand cmd, CancellationToken ct = default)
    {
        var partition = $"org-{cmd.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(cmd.ReportId, partition, ct)
            ?? throw new KeyNotFoundException($"El informe '{cmd.ReportId}' no existe.");

        // Observations + informe → final.
        foreach (var obsId in report.ResultObservationIds)
        {
            await hapi.PatchResourceStatusAsync("Observation", obsId, "final", partition, ct);
        }
        await hapi.PatchResourceStatusAsync("DiagnosticReport", cmd.ReportId, "final", partition, ct);

        // Provenance (firma de validación) best-effort: requiere el Practitioner del validador.
        var assignment = await assignments.GetAsync(cmd.UserId, cmd.OrganizationId, ct);
        if (assignment is not null && !string.IsNullOrWhiteSpace(assignment.PractitionerFhirId))
        {
            try
            {
                await hapi.CreateProvenanceAsync(new CreateProvenanceRequest(
                    PartitionName: partition,
                    TargetReference: $"DiagnosticReport/{cmd.ReportId}",
                    ActivityCode: "VERIFY",
                    ActivityDisplay: "Validación y liberación",
                    AgentWhoReference: $"Practitioner/{assignment.PractitionerFhirId}",
                    Recorded: timeProvider.GetUtcNow().ToString("o")), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo registrar Provenance de validación para {Report} (best-effort).", cmd.ReportId);
            }
        }

        // Cerrar el intake (cierra el reloj de TAT).
        if (!string.IsNullOrWhiteSpace(report.ServiceRequestFhirId))
        {
            var intake = await ops.GetIntakeByServiceRequestAsync(cmd.OrganizationId, report.ServiceRequestFhirId!, ct);
            if (intake is not null)
            {
                intake.MarkCompleted(timeProvider);
                await ops.UpdateIntakeAsync(intake, ct);
            }
        }

        return new ReleaseReportResult(cmd.ReportId, "final");
    }
}
