using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabRules;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabResults;

/// <summary>
/// Captura de resultados (Épica LIS-4, L4-F2): crea N `Observation` (con flag de
/// rango calculado) + un `DiagnosticReport` (status `preliminary`). Distingue
/// in-house vs lab externo según el `order_intake.routing` (LIS-DEC-15).
/// </summary>
public sealed class EnterResultsHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    IUserPractitionerAssignmentRepository assignments,
    IResultEvaluationService evaluation,
    ILabRuleRepository ruleRepository,
    TimeProvider timeProvider)
{
    public async Task<EnterResultsResult> HandleAsync(EnterResultsCommand cmd, CancellationToken ct = default)
    {
        if (cmd.Results.Count == 0)
        {
            throw new ArgumentException("Se requiere al menos una línea de resultado.", nameof(cmd));
        }
        var partition = $"org-{cmd.OrganizationId}";

        var order = await hapi.GetLabOrderAsync(cmd.ServiceRequestId, partition, ct)
            ?? throw new KeyNotFoundException($"La orden '{cmd.ServiceRequestId}' no existe.");

        var intake = await ops.GetIntakeByServiceRequestAsync(cmd.OrganizationId, cmd.ServiceRequestId, ct)
            ?? throw new InvalidOperationException("La orden no fue recibida; registrá la recepción primero.");
        if (intake.Status == OrderIntakeStatus.Blocked)
        {
            throw new InvalidOperationException("La orden está bloqueada (precondición no cumplida).");
        }

        var inHouse = intake.Routing == OrderRouting.InHouse;
        var specimenFhirId = inHouse ? intake.SpecimenFhirId : null;

        // Demografía del paciente para el flag (best-effort).
        var patient = await hapi.GetPatientAsync(order.PatientFhirId, partition, ct);
        var sex = ResultInterpreter.ParseSex(patient?.Gender);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var ageYears = ResultInterpreter.AgeYearsFromBirthDate(patient?.BirthDate, today);

        // Performer: in-house → Practitioner del técnico (best-effort).
        string? performerRef = null;
        if (inHouse)
        {
            var assignment = await assignments.GetAsync(cmd.UserId, cmd.OrganizationId, ct);
            if (assignment is not null && !string.IsNullOrWhiteSpace(assignment.PractitionerFhirId))
            {
                performerRef = $"Practitioner/{assignment.PractitionerFhirId}";
            }
        }

        var now = timeProvider.GetUtcNow().ToString("o");
        var genderString = patient?.Gender ?? string.Empty;
        var observations = new List<EnteredObservation>();
        var observationIds = new List<string>();
        var executions = new List<RuleExecution>();
        var hasCritical = false;
        var requiresReview = false;
        var allAutoValidated = true;

        foreach (var line in cmd.Results)
        {
            string? flagCode = null;
            decimal? low = null, high = null;
            string? appliesToSex = null;
            int? appliesAgeMin = null, appliesAgeMax = null;

            if (line.ValueNumeric is { } value && !string.IsNullOrWhiteSpace(line.Loinc))
            {
                var test = await catalog.GetTestByLoincAsync(cmd.OrganizationId, line.Loinc, ct);
                if (test is not null)
                {
                    var ranges = await catalog.ListRangesByTestAsync(cmd.OrganizationId, test.Id, ct);
                    var interp = ResultInterpreter.Interpret(ranges, sex, ageYears, value);
                    flagCode = interp.Code;
                    low = interp.Low;
                    high = interp.High;
                    appliesToSex = interp.AppliesToSex?.ToString();
                    appliesAgeMin = interp.AppliesToAgeMin;
                    appliesAgeMax = interp.AppliesToAgeMax;
                }
            }

            // Delta check: valor previo del paciente para el LOINC.
            decimal? prior = null;
            if (line.ValueNumeric is not null && !string.IsNullOrWhiteSpace(line.Loinc))
            {
                prior = await hapi.GetLatestNumericObservationAsync(order.PatientFhirId, line.Loinc!, partition, ct);
            }

            // Motor de reglas → verdict (auto-validación / revisión / crítico).
            var verdict = await evaluation.EvaluateAsync(new ResultEvaluationInput(
                cmd.OrganizationId, line.Loinc, line.ValueNumeric, flagCode, genderString, ageYears, prior), ct);

            if (verdict.IsCritical)
            {
                hasCritical = true;
            }
            if (verdict.RequiresReview)
            {
                requiresReview = true;
            }
            if (!verdict.AutoValidated)
            {
                allAutoValidated = false;
            }
            var obsStatus = verdict.AutoValidated ? "final" : "preliminary";

            var obsId = await hapi.CreateObservationAsync(new UpsertObservationRequest(
                PartitionName: partition,
                PatientFhirId: order.PatientFhirId,
                Status: obsStatus,
                LoincCode: line.Loinc,
                CodeText: line.CodeText,
                ValueNumeric: line.ValueNumeric,
                ValueUnit: line.Unit,
                ValueString: line.ValueString,
                InterpretationCode: flagCode,
                ReferenceLow: low,
                ReferenceHigh: high,
                SpecimenFhirId: specimenFhirId,
                EffectiveDateTime: now,
                PerformerReference: performerRef,
                ServiceRequestFhirId: cmd.ServiceRequestId,
                AppliesToSex: appliesToSex,
                AppliesToAgeMin: appliesAgeMin,
                AppliesToAgeMax: appliesAgeMax), ct);

            observationIds.Add(obsId);
            observations.Add(new EnteredObservation(obsId, line.CodeText, flagCode, verdict.AutoValidated));
            foreach (var er in verdict.Rules)
            {
                executions.Add(RuleExecution.Create(
                    cmd.OrganizationId, obsId, er.RuleId, er.Version, er.Fired, er.Action, timeProvider));
            }
        }

        var reportStatus = allAutoValidated ? "final" : "preliminary";

        // Panel: con varias observaciones, crear una Observation "grouper" value-less
        // (código LOINC del panel) con hasMember → componentes (LIS-RD-05, IG europeo).
        // result[] del informe = grouper + atómicas.
        var resultIdsForReport = new List<string>(observationIds);
        if (observationIds.Count > 1)
        {
            var grouperId = await hapi.CreateObservationAsync(new UpsertObservationRequest(
                PartitionName: partition,
                PatientFhirId: order.PatientFhirId,
                Status: reportStatus,
                LoincCode: order.LoincCode,
                CodeText: order.CodeText ?? cmd.Results[0].CodeText,
                ValueNumeric: null,
                ValueUnit: null,
                ValueString: null,
                InterpretationCode: null,
                ReferenceLow: null,
                ReferenceHigh: null,
                SpecimenFhirId: specimenFhirId,
                EffectiveDateTime: now,
                PerformerReference: performerRef,
                ServiceRequestFhirId: cmd.ServiceRequestId,
                HasMemberObservationIds: observationIds), ct);
            resultIdsForReport.Insert(0, grouperId);
        }

        var reportId = await hapi.CreateDiagnosticReportAsync(new UpsertDiagnosticReportRequest(
            PartitionName: partition,
            PatientFhirId: order.PatientFhirId,
            ServiceRequestFhirId: cmd.ServiceRequestId,
            Status: reportStatus,
            LoincCode: order.LoincCode,
            CodeText: order.CodeText ?? cmd.Results[0].CodeText,
            ResultObservationIds: resultIdsForReport,
            SpecimenFhirId: specimenFhirId,
            PerformerReference: performerRef,
            PerformerDisplay: inHouse ? null : (cmd.ExternalLabName ?? "Laboratorio externo"),
            Conclusion: cmd.Conclusion,
            EffectiveDateTime: now), ct);

        await ops.AddSpecimenEventAsync(
            SpecimenEvent.Create(intake.Id, SpecimenEventType.InProcess, cmd.UserId, timeProvider: timeProvider), ct);
        await ruleRepository.AddRuleExecutionsAsync(executions, ct);

        return new EnterResultsResult(
            reportId, inHouse ? "InHouse" : "External", reportStatus, hasCritical, requiresReview, observations);
    }
}
