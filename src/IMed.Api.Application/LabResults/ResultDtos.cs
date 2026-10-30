namespace IMed.Api.Application.LabResults;

/// <summary>Una línea de resultado a capturar (Épica LIS-4).</summary>
/// <param name="LabTestId">
/// Prueba del catálogo a la que corresponde el valor, como la trae la hoja de captura. Es lo que
/// elige los rangos: el LOINC es opcional en el catálogo, y una prueba sin LOINC quedaba sin
/// interpretar. Sin él (clientes viejos) se resuelve por LOINC como antes.
/// </param>
public sealed record ResultLine(
    string? Loinc,
    string CodeText,
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit,
    Guid? LabTestId = null);

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
