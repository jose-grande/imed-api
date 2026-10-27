namespace IMed.Api.Application.Preclinic;

/// <summary>
/// Lo que la pantalla de preclínica necesita ver antes de capturar (Épica P-1): las alergias que
/// el paciente ya tiene registradas —para no volver a crearlas— y los signos vitales que ya se le
/// tomaron en esta visita, si la auxiliar vuelve a abrir la preclínica.
/// </summary>
public sealed record PreclinicContextDto(
    string AppointmentId,
    string AppointmentStatus,
    string? AppointmentStart,
    string PatientFhirId,
    string? PatientDisplayName,
    string? PatientIdentifier,
    string? EncounterFhirId,
    bool NoKnownAllergies,
    IReadOnlyList<PreclinicAllergyDto> Allergies,
    IReadOnlyList<PreclinicVitalDto> Vitals,
    IReadOnlyList<PreclinicMedicationDto> Medications);

/// <summary>Medicación habitual ya registrada (Épica P-2): se muestra para no volver a cargarla.</summary>
public sealed record PreclinicMedicationDto(
    string Id,
    string? Code,
    string Display,
    string? DosageText,
    string Status);

public sealed record PreclinicAllergyDto(
    string Id,
    string? Code,
    string? System,
    string Display,
    string? Category,
    string? VerificationStatus);

public sealed record PreclinicVitalDto(
    string Id,
    string LoincCode,
    decimal Value,
    string? Unit,
    DateTimeOffset? EffectiveAt);

/// <summary>Signos vitales capturados. Todos opcionales: se emite una Observation por cada uno presente.</summary>
public sealed record PreclinicVitalsInput(
    decimal? WeightKg,
    decimal? HeightCm,
    decimal? SystolicBp,
    decimal? DiastolicBp,
    decimal? TemperatureC,
    decimal? HeartRate);

/// <summary>Alergia elegida del catálogo SNOMED (Épica J1).</summary>
public sealed record PreclinicCatalogAllergyInput(string Code, string Display, string? Category);

/// <summary>
/// Medicamento que el paciente dice tomar (Épica P-2). <c>AtcCode</c> si vino del catálogo; si no,
/// solo el texto. Lo que declara la preclínica queda en borrador hasta que el médico lo concilie.
/// </summary>
public sealed record PreclinicMedicationInput(string? AtcCode, string Display, string? DosageText);

public sealed record SubmitPreclinicCommand(
    Guid OrganizationId,
    string AppointmentId,
    string? PerformerPractitionerId,
    PreclinicVitalsInput? Vitals,
    IReadOnlyList<PreclinicCatalogAllergyInput>? CatalogAllergies,
    IReadOnlyList<string>? FreeTextAllergies,
    bool NoKnownAllergies,
    IReadOnlyList<PreclinicMedicationInput>? Medications = null);

public sealed record PreclinicSubmitResultDto(
    string EncounterFhirId,
    int ObservationsCreated,
    int AllergiesCreated,
    int MedicationsCreated,
    IReadOnlyList<string> SkippedDuplicates,
    bool NoKnownAllergiesRecorded,
    bool AppointmentCheckedIn);

/// <summary>La cita no existe en la organización.</summary>
public sealed class PreclinicAppointmentNotFoundException(string appointmentId)
    : Exception($"No existe la cita {appointmentId}.");

/// <summary>
/// La cita existe pero no está en la fila de preclínica (<c>arrived</c> o <c>checked-in</c>). Es la
/// frontera del permiso <c>record.preclinic</c>: fuera de la fila no se lee nada del paciente.
/// </summary>
public sealed class PreclinicAppointmentNotInQueueException(string status)
    : Exception($"La cita está en estado \"{status}\" y no en la fila de preclínica.");
