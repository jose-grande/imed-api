using IMed.Api.Application.Abstractions;
using IMed.Api.Application.LabReception;

namespace IMed.Api.Application.LabResults;

public sealed record CorrectResultCommand(
    Guid OrganizationId,
    Guid UserId,
    string ObservationId,
    decimal? ValueNumeric,
    string? ValueString,
    string? Unit,
    string Reason);

public sealed record CorrectResultDto(
    string ObservationId,
    string Status,
    string? FlagCode,
    decimal? ReferenceLow,
    decimal? ReferenceHigh);

/// <summary>
/// Corrección de un resultado ya capturado pero <b>aún no liberado</b> (LIS-4b).
///
/// <para>Antes no existía salida: guardado el resultado, la pantalla pasaba a solo lectura y un
/// dígito mal tecleado no se podía enmendar. Ahora el técnico corrige mientras el informe siga
/// preliminar, con motivo obligatorio.</para>
///
/// <para>Una vez <b>liberado</b> (validado y firmado, LIS-6) queda bloqueado: el resultado ya
/// salió del laboratorio y enmendarlo es otro acto —con aviso al médico— que no corresponde
/// resolver desde la pantalla de captura.</para>
///
/// <para>El valor previo no se pierde: se hace PUT sobre el mismo <c>Observation</c> y HAPI lo
/// versiona, así que la versión anterior queda en <c>_history</c>. El flag se <b>recalcula</b>
/// contra los rangos del paciente — corregir el valor sin recalcular dejaría un flag mintiendo.</para>
/// </summary>
public sealed class CorrectResultHandler(
    IHapiFhirClient hapi,
    ILabCatalogRepository catalog,
    TimeProvider timeProvider)
{
    public async Task<CorrectResultDto?> HandleAsync(CorrectResultCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cmd.Reason))
        {
            throw new ArgumentException("La corrección requiere un motivo.", nameof(cmd));
        }
        if (cmd.ValueNumeric is null && string.IsNullOrWhiteSpace(cmd.ValueString))
        {
            throw new ArgumentException("Indicá el valor corregido.", nameof(cmd));
        }

        var partition = LabRouting.PartitionOf(cmd.OrganizationId);

        var observation = await hapi.GetObservationAsync(cmd.ObservationId, partition, ct);
        if (observation is null)
        {
            return null;
        }

        // Un resultado ya liberado salió del laboratorio: no se re-escribe desde acá.
        if (string.Equals(observation.Status, "final", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "El resultado ya fue liberado y no se puede corregir desde la captura. Requiere una enmienda del informe.");
        }

        // Recalcular el flag con el valor corregido y los rangos de ESTE paciente.
        var patient = await hapi.GetPatientAsync(observation.PatientFhirId, partition, ct);
        var sex = ResultInterpreter.ParseSex(patient?.Gender);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var ageYears = ResultInterpreter.AgeYearsFromBirthDate(patient?.BirthDate, today);

        string? flagCode = null;
        decimal? low = null, high = null;
        string? appliesToSex = null;
        int? appliesAgeMin = null, appliesAgeMax = null;

        if (cmd.ValueNumeric is { } value && !string.IsNullOrWhiteSpace(observation.LoincCode))
        {
            var test = await catalog.GetTestByLoincAsync(cmd.OrganizationId, observation.LoincCode!, ct);
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

        // FHIR marca el resultado rectificado con status `corrected`: el consumidor sabe que
        // este valor reemplaza a uno emitido antes, no que es una primera carga.
        const string CorrectedStatus = "corrected";
        var now = timeProvider.GetUtcNow().ToString("o");

        await hapi.UpdateObservationAsync(cmd.ObservationId, new UpsertObservationRequest(
            PartitionName: partition,
            PatientFhirId: observation.PatientFhirId,
            Status: CorrectedStatus,
            LoincCode: observation.LoincCode,
            CodeText: observation.CodeText ?? "Resultado",
            ValueNumeric: cmd.ValueNumeric,
            ValueUnit: cmd.Unit ?? observation.ValueUnit,
            ValueString: cmd.ValueString,
            InterpretationCode: flagCode,
            ReferenceLow: low,
            ReferenceHigh: high,
            // El PUT reemplaza el recurso entero: los vínculos van de vuelta tal cual estaban,
            // porque perderlos desconectaría el resultado de su orden y su muestra.
            SpecimenFhirId: observation.SpecimenFhirId,
            EffectiveDateTime: now,
            PerformerReference: observation.PerformerReference,
            ServiceRequestFhirId: observation.ServiceRequestFhirId,
            AppliesToSex: appliesToSex,
            AppliesToAgeMin: appliesAgeMin,
            AppliesToAgeMax: appliesAgeMax), ct);

        return new CorrectResultDto(cmd.ObservationId, CorrectedStatus, flagCode, low, high);
    }
}
