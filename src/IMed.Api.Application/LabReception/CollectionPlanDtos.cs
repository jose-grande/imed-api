using IMed.Api.Domain.Lab;

namespace IMed.Api.Application.LabReception;

/// <summary>Plan de colección calculado para una requisición (R6-F2).</summary>
public sealed record CollectionPlanDto(
    Guid Id,
    string Requisition,
    string? EncounterFhirId,
    string PatientFhirId,
    string Status,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CollectionPlanContainerDto> Containers,
    IReadOnlyList<CollectionPlanUnavailableItemDto> UnavailableItems)
{
    public static CollectionPlanDto FromDomain(CollectionPlan p) => new(
        p.Id, p.Requisition, p.EncounterFhirId, p.PatientFhirId, p.Status.ToString(), p.CreatedAt,
        p.Containers.OrderBy(c => c.Sequence).Select(CollectionPlanContainerDto.FromDomain).ToList(),
        p.UnavailableItems.Select(CollectionPlanUnavailableItemDto.FromDomain).ToList());
}

/// <summary>Prueba/panel de la orden que no se pudo planificar por no estar en el
/// catálogo del tenant (rev 54). El workbench la muestra para referirla o agregarla.</summary>
public sealed record CollectionPlanUnavailableItemDto(
    string ServiceRequestFhirId,
    string? LoincCode,
    string Display,
    string Reason)
{
    public static CollectionPlanUnavailableItemDto FromDomain(CollectionPlanUnavailableItem i) => new(
        i.ServiceRequestFhirId, i.LoincCode, i.Display, i.Reason);
}

/// <summary>Un contenedor físico del plan (tubo/recipiente) y las pruebas que cubre.</summary>
public sealed record CollectionPlanContainerDto(
    Guid Id,
    int Sequence,
    string SpecimenTypeCode,
    string? SpecimenTypeName,
    string? TubeTypeCode,
    string? TubeTypeName,
    string? Additive,
    decimal RequiredVolumeMl,
    string Status,
    string? AccessionNumber,
    string? Barcode,
    DateTimeOffset? CollectedAt,
    DateTimeOffset? ReceivedAt,
    string? StorageLocation,
    string? RejectionReasonCode,
    IReadOnlyList<CollectionPlanItemDto> Items)
{
    public static CollectionPlanContainerDto FromDomain(CollectionPlanContainer c) => new(
        c.Id, c.Sequence, c.SpecimenTypeCode, c.SpecimenTypeName, c.TubeTypeCode, c.TubeTypeName,
        c.Additive, c.RequiredVolumeMl, c.Status.ToString(), c.AccessionNumber, c.Barcode,
        c.CollectedAt, c.ReceivedAt, c.StorageLocation, c.RejectionReasonCode,
        c.Items.Select(CollectionPlanItemDto.FromDomain).ToList());
}

public sealed record CollectionPlanItemDto(
    string ServiceRequestFhirId,
    string LabTestCode,
    string? LabTestName)
{
    public static CollectionPlanItemDto FromDomain(CollectionPlanItem i) => new(
        i.ServiceRequestFhirId, i.LabTestCode, i.LabTestName);
}

/// <summary>Resultado del control de calidad preanalítico de un contenedor (R6-F6).
/// Si fue rechazado, indica el motivo y si exige solicitar una nueva muestra.</summary>
public sealed record QualityControlResultDto(
    CollectionPlanContainerDto Container,
    bool Rejected,
    bool RequiresNewSample,
    string? RejectionReasonName);

/// <summary>Evento del timeline de cadena de custodia de un contenedor (R6-F3).</summary>
public sealed record CollectionEventDto(
    Guid Id,
    string Type,
    DateTimeOffset At,
    Guid? By,
    string? Detail)
{
    public static CollectionEventDto FromDomain(CollectionEvent e) => new(
        e.Id, e.Type.ToString(), e.At, e.By, e.Detail);
}
