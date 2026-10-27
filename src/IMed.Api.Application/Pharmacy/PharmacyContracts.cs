namespace IMed.Api.Application.Pharmacy;

/// <summary>Un medicamento recetado (<c>MedicationRequest</c>), como lo necesita la farmacia.</summary>
public sealed record PrescribedMedication(
    string Id,
    string PatientFhirId,
    string? EncounterFhirId,
    string Status,
    string? AtcCode,
    string? MedicationDisplay,
    string? MedicationText,
    string? DosageText,
    decimal? Quantity,
    string? Unit,
    string? GroupId,
    string? RequesterPractitionerId,
    DateTimeOffset? AuthoredOn)
{
    /// <summary>Lo que se le muestra a farmacia como nombre de lo recetado.</summary>
    public string Label => MedicationText ?? MedicationDisplay ?? AtcCode ?? "Medicamento sin nombre";
}

/// <summary>
/// Puerto hacia las recetas de las consultas (FSF §10.2: la farmacia <b>consume</b>
/// <c>MedicationRequest</c>, no lo crea). Vive en el módulo de farmacia para no tocar el cliente
/// HAPI compartido: su implementación se arma sobre las lecturas genéricas que ya existen.
/// </summary>
public interface IPrescriptionSource
{
    /// <summary>Recetas activas emitidas desde <paramref name="since"/>, las más recientes primero.</summary>
    Task<IReadOnlyList<PrescribedMedication>> ListActiveAsync(
        Guid organizationId, DateTimeOffset since, CancellationToken ct = default);

    Task<PrescribedMedication?> GetAsync(Guid organizationId, string medicationRequestId, CancellationToken ct = default);

    /// <summary>
    /// Sede de cada consulta (<c>Encounter.location</c> → id de <c>organization_location</c>). Null
    /// si la consulta no declara sede (anteriores a FAC-9) o no existe.
    /// </summary>
    Task<IReadOnlyDictionary<string, Guid?>> GetEncounterLocationsAsync(
        Guid organizationId, IReadOnlyCollection<string> encounterIds, CancellationToken ct = default);

    /// <summary>Nombre para mostrar de cada paciente.</summary>
    Task<IReadOnlyDictionary<string, string?>> GetPatientNamesAsync(
        Guid organizationId, IReadOnlyCollection<string> patientIds, CancellationToken ct = default);
}

/// <summary>Un artículo que puede despacharse para una receta, con lo que hay en la bodega.</summary>
public sealed record DispenseCandidateDto(
    Guid StockItemId,
    string Code,
    string Name,
    string BaseUnit,
    string? AtcCode,
    decimal Usable,
    decimal? Price,
    bool PricePending,
    bool Sellable);

public sealed record QueueMedicationDto(
    string MedicationRequestId,
    string Label,
    string? AtcCode,
    string? DosageText,
    decimal? PrescribedQuantity,
    string? PrescribedUnit,
    decimal AlreadyDispensed,
    IReadOnlyList<DispenseCandidateDto> Candidates,
    Guid? ProposedStockItemId);

/// <summary>Una receta pendiente en la cola de farmacia: una consulta y sus medicamentos por despachar.</summary>
public sealed record QueuePrescriptionDto(
    string EncounterFhirId,
    string PatientFhirId,
    string? PatientName,
    string? GroupId,
    DateTimeOffset? AuthoredOn,
    IReadOnlyList<QueueMedicationDto> Medications);

public sealed record PrescriptionQueueDto(Guid LocationId, Guid WarehouseId, string WarehouseName, IReadOnlyList<QueuePrescriptionDto> Prescriptions);

public sealed record DispenseLotDto(Guid StockLotId, string LotCode, DateOnly? ExpiryDate, decimal Quantity);

public sealed record DispenseLineDto(
    Guid Id,
    Guid StockItemId,
    string Description,
    decimal Quantity,
    string BaseUnit,
    string? MedicationRequestFhirId,
    string? PrescribedText,
    decimal? PrescribedQuantity,
    string? PrescribedUnit,
    bool CompletesPrescription,
    string? SubstitutionReason,
    Guid? ChargeId,
    decimal UnitPrice,
    decimal Total,
    bool PricePending,
    IReadOnlyList<DispenseLotDto> Lots);

public sealed record DispenseDto(
    Guid Id,
    string Kind,
    string Status,
    Guid LocationId,
    Guid WarehouseId,
    string WarehouseName,
    Guid AccountId,
    string? PatientFhirId,
    string? PatientName,
    string? EncounterFhirId,
    string? BuyerName,
    string? BuyerTaxId,
    DateTimeOffset DispensedAt,
    Guid? DispensedBy,
    string? Notes,
    Guid? InvoiceId,
    string? CancellationReason,
    decimal Total,
    IReadOnlyList<DispenseLineDto> Lines);

/// <summary>Resultado de un despacho: el acto y si ya existía (reintento con la misma llave).</summary>
public sealed record DispenseResult(DispenseDto Dispense, bool AlreadyProcessed);
