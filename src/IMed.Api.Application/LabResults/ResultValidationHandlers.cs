using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Lab;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.LabResults;

public sealed record HistoryPointDto(decimal Value, string? Date, string? FlagCode);
public sealed record ReleaseReportResult(string ReportId, string Status);

// --------------------------- Pendientes ---------------------------

public sealed record GetPendingValidationQuery(Guid OrganizationId);

public sealed class GetPendingValidationHandler(IHapiFhirClient hapi, ILabOperationsRepository ops)
{
    public async Task<IReadOnlyList<LabReportSummaryDto>> HandleAsync(GetPendingValidationQuery q, CancellationToken ct = default)
    {
        var reports = await hapi.SearchPreliminaryReportsAsync($"org-{q.OrganizationId}", ct);

        // Un informe devuelto al técnico está en manos de él, no del validador: sale de la cola
        // hasta que la corrección cierre la devolución. Si no, el validador vuelve a ver una y
        // otra vez el mismo caso que ya rechazó.
        var open = await ops.ListOpenValidationRejectionsAsync(q.OrganizationId, ct);
        var returned = open.Select(r => r.ReportFhirId).ToHashSet(StringComparer.Ordinal);

        return reports
            .Where(r => !returned.Contains(r.Id))
            .Select(r => new LabReportSummaryDto(r.Id, r.Status, r.LoincCode, r.CodeText, r.Issued, r.Conclusion))
            .ToList();
    }
}

// --------------------------- Devolución al técnico ---------------------------

public sealed record RejectReportCommand(
    Guid OrganizationId, Guid UserId, string ReportId, string ReasonCode, string? Note);

public sealed record RejectReportResult(string ReportId, string ReasonCode, string ReasonName, bool RequiresNewSample);

/// <summary>
/// Devuelve un informe al técnico en vez de liberarlo.
///
/// <para>Es el desenlace que le faltaba a la estación de Validación: sus únicas opciones eran
/// liberar o irse, y irse no registraba nada. Con esto, "no lo firmo" deja rastro: quién, cuándo,
/// por qué (código de catálogo) y con qué detalle.</para>
///
/// <para><b>No cambia el status FHIR.</b> El informe sigue <c>preliminary</c>, que es lo correcto:
/// no fue anulado ni corregido, simplemente todavía no lo firma nadie. Lo que cambia es de quién
/// es el turno, y eso vive en <c>result_validation_rejection</c>.</para>
/// </summary>
public sealed class RejectReportHandler(
    IHapiFhirClient hapi,
    ILabOperationsRepository ops,
    IUserPractitionerAssignmentRepository assignments,
    TimeProvider timeProvider,
    ILogger<RejectReportHandler> logger)
{
    public async Task<RejectReportResult> HandleAsync(RejectReportCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.ReasonCode))
        {
            throw new ArgumentException("El motivo de devolución es obligatorio.", nameof(cmd));
        }

        var partition = $"org-{cmd.OrganizationId}";
        var report = await hapi.GetDiagnosticReportAsync(cmd.ReportId, partition, ct)
            ?? throw new KeyNotFoundException($"El informe '{cmd.ReportId}' no existe.");

        if (string.Equals(report.Status, "final", StringComparison.OrdinalIgnoreCase))
        {
            // Un informe liberado ya salió del laboratorio: corregirlo es una enmienda, otro acto.
            throw new InvalidOperationException(
                "El informe ya fue liberado; una corrección posterior es una enmienda, no una devolución.");
        }

        var reason = await ops.GetValidationRejectionReasonByCodeAsync(cmd.OrganizationId, cmd.ReasonCode, ct)
            ?? throw new InvalidOperationException($"El motivo '{cmd.ReasonCode}' no existe en el catálogo.");

        var open = await ops.ListOpenValidationRejectionsAsync(cmd.OrganizationId, ct);
        if (open.Any(r => string.Equals(r.ReportFhirId, cmd.ReportId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("El informe ya está devuelto al técnico.");
        }

        await ops.AddValidationRejectionAsync(
            ResultValidationRejection.Create(
                cmd.OrganizationId, cmd.ReportId, report.ServiceRequestFhirId,
                reason.Id, cmd.Note, cmd.UserId, timeProvider),
            ct);

        // Firma de la devolución: quién no lo validó también es parte de la trazabilidad.
        var assignment = await assignments.GetAsync(cmd.UserId, cmd.OrganizationId, ct);
        if (assignment is not null && !string.IsNullOrWhiteSpace(assignment.PractitionerFhirId))
        {
            try
            {
                await hapi.CreateProvenanceAsync(new CreateProvenanceRequest(
                    PartitionName: partition,
                    TargetReference: $"DiagnosticReport/{cmd.ReportId}",
                    ActivityCode: "REVIEW",
                    ActivityDisplay: $"Devuelto al técnico: {reason.Name}",
                    AgentWhoReference: $"Practitioner/{assignment.PractitionerFhirId}",
                    Recorded: timeProvider.GetUtcNow().ToString("o")), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo registrar Provenance de devolución para {Report} (best-effort).", cmd.ReportId);
            }
        }

        return new RejectReportResult(cmd.ReportId, reason.Code, reason.Name, reason.RequiresNewSample);
    }
}

// --------------------------- Catálogo de motivos ---------------------------

public sealed record ValidationRejectionReasonDto(
    string Code, string Name, string? Description, bool RequiresNewSample);

public sealed class GetValidationRejectionReasonsHandler(ILabOperationsRepository ops)
{
    public async Task<IReadOnlyList<ValidationRejectionReasonDto>> HandleAsync(
        Guid organizationId, CancellationToken ct = default) =>
        (await ops.ListValidationRejectionReasonsAsync(organizationId, ct))
            .Select(r => new ValidationRejectionReasonDto(r.Code, r.Name, r.Description, r.RequiresNewSample))
            .ToList();
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

public sealed record ReleaseReportCommand(
    Guid OrganizationId, Guid UserId, string ReportId,
    /// <summary>
    /// Comentario del validador al firmar. Va al <c>conclusion</c> del informe, así que viaja
    /// con el resultado y sale impreso — no queda escondido en una tabla interna.
    /// </summary>
    string? Comment = null);

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

        if (!string.IsNullOrWhiteSpace(cmd.Comment))
        {
            // Best-effort: si el comentario no se puede escribir, no se cae la liberación —
            // el informe firmado importa más que la nota.
            try
            {
                await hapi.PatchDiagnosticReportConclusionAsync(cmd.ReportId, cmd.Comment!.Trim(), partition, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "No se pudo guardar el comentario de validación de {Report}.", cmd.ReportId);
            }
        }

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
                // Quién firma queda registrado acá y no solo en el Provenance: es el dato que
                // el informe impreso necesita en cada reimpresión.
                intake.MarkValidated(assignment?.PractitionerFhirId, timeProvider);
                await ops.UpdateIntakeAsync(intake, ct);
            }
        }

        return new ReleaseReportResult(cmd.ReportId, "final");
    }
}
