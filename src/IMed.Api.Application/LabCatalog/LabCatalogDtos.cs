using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabCatalog;

/// <summary>DTO de una prueba de laboratorio (Épica LIS-1).</summary>
public sealed record LabTestDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string? LoincCode,
    string Name,
    string Datatype,
    string? Unit,
    string Department,
    int? FastingHours,
    string? CollectionType,
    string? PatientInstruction,
    bool Active)
{
    public static LabTestDto FromDomain(LabTestDefinition t) => new(
        t.Id, t.OrganizationId, t.Code, t.LoincCode, t.Name,
        t.Datatype.ToString(), t.Unit, t.Department.ToString(),
        t.FastingHours, t.CollectionType, t.PatientInstruction, t.Active);
}

/// <summary>Resumen de un panel para listados.</summary>
public sealed record LabPanelDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string Name,
    string? Description,
    string? LoincCode,
    bool Active)
{
    public static LabPanelDto FromDomain(LabPanel p) => new(
        p.Id, p.OrganizationId, p.Code, p.Name, p.Description, p.LoincCode, p.Active);
}

/// <summary>Item de una versión de panel (prueba + orden).</summary>
public sealed record LabPanelItemDto(
    Guid LabTestId,
    string Code,
    string Name,
    string? Unit,
    int DisplayOrder);

/// <summary>Detalle de un panel con su versión activa y composición de pruebas.</summary>
public sealed record LabPanelDetailDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string Name,
    string? Description,
    bool Active,
    int? ActiveVersion,
    DateOnly? EffectiveDate,
    IReadOnlyList<LabPanelItemDto> Items);

/// <summary>Rango de referencia (incluye valores críticos).</summary>
public sealed record ReferenceRangeDto(
    Guid Id,
    Guid? OrganizationId,
    Guid LabTestId,
    string Sex,
    int? AgeMinYears,
    int? AgeMaxYears,
    bool? Pregnancy,
    string? Method,
    decimal? Low,
    decimal? High,
    decimal? CriticalLow,
    decimal? CriticalHigh,
    string? Unit)
{
    public static ReferenceRangeDto FromDomain(ReferenceRange r) => new(
        r.Id, r.OrganizationId, r.LabTestId, r.Sex.ToString(),
        r.AgeMinYears, r.AgeMaxYears, r.Pregnancy, r.Method,
        r.Low, r.High, r.CriticalLow, r.CriticalHigh, r.Unit);
}

/// <summary>Fórmula calculada (read-only en LIS-1).</summary>
public sealed record DerivedCalculationDto(
    Guid Id,
    Guid? OrganizationId,
    string OutputTestCode,
    string Name,
    string FormulaJson,
    string InputsJson,
    bool Active)
{
    public static DerivedCalculationDto FromDomain(DerivedCalculation d) => new(
        d.Id, d.OrganizationId, d.OutputTestCode, d.Name, d.FormulaJson, d.InputsJson, d.Active);
}

/// <summary>Objetivo de TAT (read-only en LIS-1).</summary>
public sealed record TatTargetDto(
    Guid Id,
    Guid? OrganizationId,
    string Scope,
    string TargetCode,
    string Priority,
    int Minutes)
{
    public static TatTargetDto FromDomain(TatTarget t) => new(
        t.Id, t.OrganizationId, t.Scope.ToString(), t.TargetCode, t.Priority.ToString(), t.Minutes);
}

/// <summary>Tipo de muestra del catálogo (LIS-RD-26).</summary>
public sealed record SpecimenTypeDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string Name,
    string? SnomedCode,
    string CollectionMode,
    string? DefaultMethod,
    string? DefaultBodySite,
    string ContainerKind,
    int? StabilityMinutes,
    string Discipline,
    bool Active)
{
    public static SpecimenTypeDto FromDomain(SpecimenType s) => new(
        s.Id, s.OrganizationId, s.Code, s.Name, s.SnomedCode,
        s.CollectionMode.ToString(), s.DefaultMethod, s.DefaultBodySite,
        s.ContainerKind.ToString(), s.StabilityMinutes, s.Discipline.ToString(), s.Active);
}

/// <summary>Tipo de tubo de sangre (LIS-RD-16).</summary>
public sealed record TubeTypeDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string Name,
    string? Color,
    string? Additive,
    decimal? VolumeMinMl,
    bool Active)
{
    public static TubeTypeDto FromDomain(TubeType t) => new(
        t.Id, t.OrganizationId, t.Code, t.Name, t.Color, t.Additive, t.VolumeMinMl, t.Active);
}
