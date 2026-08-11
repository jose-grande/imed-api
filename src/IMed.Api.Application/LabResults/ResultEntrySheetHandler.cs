using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;
using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabResults;

/// <summary>
/// Contexto de captura de resultados de una orden (LIS-4b).
///
/// <para><b>Por qué existe:</b> la pantalla de captura tenía un único campo de valor y
/// guardaba una sola línea colgada del código de la orden. Para un panel eso es incorrecto:
/// una biometría hemática son ~10 analitos, cada uno con su unidad y su rango. La expansión
/// de panel ya existía (<see cref="LabRouting.ExpandOrderAsync"/>, usada por el enrutamiento
/// y el plan de colección) pero nunca se conectó a la captura.</para>
///
/// <para>Además entrega <b>rango de referencia resuelto para este paciente</b> (sexo/edad) y
/// el <b>valor anterior</b>: verlos mientras se digita es lo que atrapa el 450 que iba a ser
/// 45. Antes el flag aparecía recién después de guardar.</para>
/// </summary>
public sealed class GetResultEntrySheetHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    ILabOperationsRepository ops,
    TimeProvider timeProvider)
{
    /// <summary>Cuántos puntos de histórico se piden para mostrar el valor anterior.</summary>
    private const int HistoryPoints = 1;

    public async Task<ResultEntrySheetDto?> HandleAsync(
        Guid orgId, string serviceRequestId, CancellationToken ct = default)
    {
        var partition = LabRouting.PartitionOf(orgId);

        var order = await hapi.GetLabOrderAsync(serviceRequestId, partition, ct);
        if (order is null)
        {
            return null;
        }

        var patient = await hapi.GetPatientAsync(order.PatientFhirId, partition, ct);
        var sex = ResultInterpreter.ParseSex(patient?.Gender);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var ageYears = ResultInterpreter.AgeYearsFromBirthDate(patient?.BirthDate, today);

        var intake = await ops.GetIntakeByServiceRequestAsync(orgId, serviceRequestId, ct);

        // Expandir la orden a sus pruebas efectivas. Un panel rinde N analitos; una prueba
        // suelta rinde una sola. Es exactamente lo que faltaba en la captura.
        var expanded = await LabRouting.ExpandOrderAsync(catalog, orgId, order.LoincCode, ct);

        var analytes = new List<ResultAnalyteDto>();
        foreach (var (test, routing) in expanded)
        {
            var ranges = await catalog.ListRangesByTestAsync(orgId, test.Id, ct);

            // Se elige el rango aplicable a ESTE paciente con la misma lógica que después
            // calculará el flag, para que lo que el técnico ve al digitar y lo que el sistema
            // decide al guardar no puedan discrepar.
            var applicable = ResultInterpreter.SelectRange(ranges, sex, ageYears);

            decimal? previousValue = null;
            string? previousDate = null;
            string? previousFlag = null;
            if (!string.IsNullOrWhiteSpace(test.LoincCode))
            {
                var history = await hapi.GetObservationHistoryAsync(
                    order.PatientFhirId, test.LoincCode!, partition, HistoryPoints, ct);
                var last = history.Count > 0 ? history[0] : null;
                if (last is not null)
                {
                    previousValue = last.Value;
                    previousDate = last.Date;
                    previousFlag = last.FlagCode;
                }
            }

            analytes.Add(new ResultAnalyteDto(
                LabTestId: test.Id,
                Code: test.Code,
                Name: test.Name,
                LoincCode: test.LoincCode,
                Datatype: test.Datatype.ToString(),
                Unit: applicable?.Unit ?? test.Unit,
                ReferenceLow: applicable?.Low,
                ReferenceHigh: applicable?.High,
                CriticalLow: applicable?.CriticalLow,
                CriticalHigh: applicable?.CriticalHigh,
                SendOut: routing == OrderRouting.SendOut,
                PreviousValue: previousValue,
                PreviousDate: previousDate,
                PreviousFlagCode: previousFlag));
        }

        // Si la orden ya se resultó, la pantalla tiene que abrir en modo revisión/corrección.
        // Antes esto no se devolvía y el estado de "guardado" solo vivía en memoria del
        // componente: salir de la pantalla lo perdía para siempre y al volver aparecía el
        // formulario en blanco, invitando a redigitar y duplicar el informe.
        var saved = intake is null
            ? null
            : await LoadSavedResultsAsync(intake, order.PatientFhirId, partition, ct);

        return new ResultEntrySheetDto(
            ServiceRequestId: serviceRequestId,
            Requisition: order.Requisition,
            PatientFhirId: order.PatientFhirId,
            PatientName: patient?.DisplayName,
            PatientBirthDate: patient?.BirthDate,
            PatientGender: patient?.Gender,
            PatientAgeYears: ageYears,
            OrderCodeText: order.CodeText,
            OrderLoincCode: order.LoincCode,
            AccessionNumber: intake?.AccessionNumber,
            Received: intake is not null && intake.Status != OrderIntakeStatus.Pending,
            Blocked: intake?.Status == OrderIntakeStatus.Blocked,
            Analytes: analytes,
            Saved: saved);
    }

    /// <summary>
    /// Trae el informe ya capturado de esta orden, con sus observaciones, para que la pantalla
    /// pueda reabrirse en modo revisión. Devuelve <c>null</c> si todavía no se resultó.
    /// </summary>
    private async Task<SavedResultsDto?> LoadSavedResultsAsync(
        OrderIntake intake, string patientFhirId, string partition, CancellationToken ct)
    {
        var reportId = await LabReportLookup.ResolveReportIdAsync(hapi, intake, patientFhirId, partition, ct);
        if (reportId is null)
        {
            return null;
        }

        var report = await hapi.GetDiagnosticReportAsync(reportId, partition, ct);
        if (report is null)
        {
            // El ancla apunta a un informe que ya no está: se trata como no resultada en vez
            // de dejar la pantalla inservible.
            return null;
        }

        var observations = new List<SavedObservationDto>();
        var hasCritical = false;
        foreach (var obsId in report.ResultObservationIds)
        {
            var obs = await hapi.GetObservationAsync(obsId, partition, ct);
            if (obs is null)
            {
                continue;
            }

            // La observación "grouper" del panel no tiene valor: es el encabezado, no una
            // línea corregible. Mostrarla como fila vacía era justamente lo que confundía.
            if (obs.ValueNumeric is null && string.IsNullOrWhiteSpace(obs.ValueString))
            {
                continue;
            }

            if (obs.FlagCode is "HH" or "LL")
            {
                hasCritical = true;
            }

            observations.Add(new SavedObservationDto(
                ObservationId: obs.Id,
                CodeText: obs.CodeText ?? obs.LoincCode ?? "—",
                ValueNumeric: obs.ValueNumeric,
                ValueString: obs.ValueString,
                Unit: obs.ValueUnit,
                FlagCode: obs.FlagCode,
                Status: obs.Status));
        }

        return new SavedResultsDto(
            ReportId: report.Id,
            Origin: intake.Routing == OrderRouting.InHouse ? "InHouse" : "External",
            ReportStatus: report.Status,
            Conclusion: report.Conclusion,
            HasCritical: hasCritical,
            Observations: observations);
    }
}

/// <summary>Resultados ya capturados de una orden — habilita reabrir en modo revisión/corrección.</summary>
public sealed record SavedResultsDto(
    string ReportId,
    string Origin,
    string ReportStatus,
    string? Conclusion,
    bool HasCritical,
    IReadOnlyList<SavedObservationDto> Observations);

/// <summary>Una observación ya guardada. Excluye el grouper del panel, que no tiene valor.</summary>
public sealed record SavedObservationDto(
    string ObservationId,
    string CodeText,
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit,
    string? FlagCode,
    string Status);

/// <summary>Todo lo que la pantalla de captura necesita para una orden (LIS-4b).</summary>
public sealed record ResultEntrySheetDto(
    string ServiceRequestId,
    string? Requisition,
    string PatientFhirId,
    string? PatientName,
    string? PatientBirthDate,
    string? PatientGender,
    int? PatientAgeYears,
    string? OrderCodeText,
    string? OrderLoincCode,
    string? AccessionNumber,
    bool Received,
    bool Blocked,
    IReadOnlyList<ResultAnalyteDto> Analytes,
    /// <summary>No nulo si la orden ya se resultó: la pantalla abre en revisión, no en captura.</summary>
    SavedResultsDto? Saved = null);

/// <summary>Una prueba a resultar, con su unidad, su rango para este paciente y su valor previo.</summary>
public sealed record ResultAnalyteDto(
    Guid LabTestId,
    string Code,
    string Name,
    string? LoincCode,
    string Datatype,
    /// <summary>Unidad del catálogo — se precarga para que nadie escriba "mg/dl" a mano.</summary>
    string? Unit,
    decimal? ReferenceLow,
    decimal? ReferenceHigh,
    decimal? CriticalLow,
    decimal? CriticalHigh,
    /// <summary>La prueba se deriva a un laboratorio externo: no se resulta acá.</summary>
    bool SendOut,
    decimal? PreviousValue,
    string? PreviousDate,
    string? PreviousFlagCode);
