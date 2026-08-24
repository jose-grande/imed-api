namespace IMed.Api.Application.LabResults;

/// <summary>Una línea de resultado a capturar (Épica LIS-4).</summary>
public sealed record ResultLine(
    string? Loinc,
    string CodeText,
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit);

public sealed record EnterResultsCommand(
    Guid OrganizationId,
    Guid UserId,
    string ServiceRequestId,
    string? ExternalLabName,
    IReadOnlyList<ResultLine> Results,
    string? Conclusion);

/// <summary>Resultado de una Observation creada (con su flag y verdict del motor).</summary>
public sealed record EnteredObservation(
    string ObservationId,
    string CodeText,
    string? FlagCode,
    bool AutoValidated);

public sealed record EnterResultsResult(
    string DiagnosticReportId,
    string Origin,
    string ReportStatus,
    bool HasCritical,
    bool RequiresReview,
    IReadOnlyList<EnteredObservation> Observations);
