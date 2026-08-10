using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabCatalog;

/// <summary>Parsing case-insensitive de enums del catálogo; lanza <see cref="ArgumentException"/> si no matchea.</summary>
internal static class LabCatalogParsing
{
    public static LabTestDatatype Datatype(string value) => ParseEnum<LabTestDatatype>(value, nameof(LabTestDatatype));
    public static LabDepartment Department(string value) => ParseEnum<LabDepartment>(value, nameof(LabDepartment));
    public static ReferenceSex Sex(string value) => ParseEnum<ReferenceSex>(value, nameof(ReferenceSex));

    /// <summary>Modo de colección opcional (override); null/vacío = hereda del tipo de muestra.</summary>
    public static CollectionMode? CollectionModeOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : ParseEnum<CollectionMode>(value, nameof(CollectionMode));

    private static TEnum ParseEnum<TEnum>(string value, string name) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            throw new ArgumentException(
                $"Valor inválido para {name}: '{value}'. Válidos: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }
        return parsed;
    }
}

// ------------------------------- Pruebas -------------------------------

public sealed record CreateLabTestCommand(
    Guid? OrganizationId, string Code, string Name, string Datatype, string? LoincCode, string? Unit,
    string Department, int? FastingHours, string? CollectionType, string? PatientInstruction,
    Guid? SpecimenTypeId, Guid? TubeTypeId, string? CollectionMode,
    decimal? MinVolumeMl, decimal? PreferredVolumeMl, decimal? AnalyzerMinVolumeMl, int? StabilityMinutes);

public sealed class CreateLabTestHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabTestDto> HandleAsync(CreateLabTestCommand cmd, CancellationToken cancellationToken = default)
    {
        if (await repo.TestCodeExistsAsync(cmd.OrganizationId, cmd.Code, cancellationToken))
        {
            throw new InvalidOperationException($"Ya existe una prueba con code='{cmd.Code}' en este alcance.");
        }
        var test = LabTestDefinition.Create(
            cmd.Code, cmd.Name, LabCatalogParsing.Datatype(cmd.Datatype), cmd.LoincCode, cmd.Unit,
            LabCatalogParsing.Department(cmd.Department), cmd.FastingHours, cmd.CollectionType,
            cmd.PatientInstruction, cmd.OrganizationId, active: true, timeProvider: timeProvider);
        test.SetSpecimenMapping(
            cmd.SpecimenTypeId, cmd.TubeTypeId, LabCatalogParsing.CollectionModeOrNull(cmd.CollectionMode), timeProvider);
        test.SetCollectionRequirements(
            cmd.MinVolumeMl, cmd.PreferredVolumeMl, cmd.AnalyzerMinVolumeMl, cmd.StabilityMinutes, timeProvider);
        await repo.AddTestAsync(test, cancellationToken);
        return LabTestDto.FromDomain(test);
    }
}

public sealed record UpdateLabTestCommand(
    Guid Id, string Name, string Datatype, string? LoincCode, string? Unit,
    string Department, int? FastingHours, string? CollectionType, string? PatientInstruction,
    Guid? SpecimenTypeId, Guid? TubeTypeId, string? CollectionMode,
    decimal? MinVolumeMl, decimal? PreferredVolumeMl, decimal? AnalyzerMinVolumeMl, int? StabilityMinutes);

public sealed class UpdateLabTestHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabTestDto> HandleAsync(UpdateLabTestCommand cmd, CancellationToken cancellationToken = default)
    {
        var test = await repo.GetTestByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prueba '{cmd.Id}' no existe.");
        test.Edit(cmd.Name, LabCatalogParsing.Datatype(cmd.Datatype), cmd.LoincCode, cmd.Unit,
            LabCatalogParsing.Department(cmd.Department), cmd.FastingHours, cmd.CollectionType,
            cmd.PatientInstruction, timeProvider);
        test.SetSpecimenMapping(
            cmd.SpecimenTypeId, cmd.TubeTypeId, LabCatalogParsing.CollectionModeOrNull(cmd.CollectionMode), timeProvider);
        test.SetCollectionRequirements(
            cmd.MinVolumeMl, cmd.PreferredVolumeMl, cmd.AnalyzerMinVolumeMl, cmd.StabilityMinutes, timeProvider);
        await repo.UpdateTestAsync(test, cancellationToken);
        return LabTestDto.FromDomain(test);
    }
}

public sealed record SetLabTestActiveCommand(Guid Id, bool Active);

public sealed class SetLabTestActiveHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabTestDto> HandleAsync(SetLabTestActiveCommand cmd, CancellationToken cancellationToken = default)
    {
        var test = await repo.GetTestByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Prueba '{cmd.Id}' no existe.");
        test.SetActive(cmd.Active, timeProvider);
        await repo.UpdateTestAsync(test, cancellationToken);
        return LabTestDto.FromDomain(test);
    }
}

// ------------------------------- Paneles -------------------------------

public sealed record CreateLabPanelCommand(
    Guid? OrganizationId, string Code, string Name, string? Description, IReadOnlyList<Guid> TestIds);

public sealed class CreateLabPanelHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabPanelDetailDto> HandleAsync(CreateLabPanelCommand cmd, CancellationToken cancellationToken = default)
    {
        if (await repo.PanelCodeExistsAsync(cmd.OrganizationId, cmd.Code, cancellationToken))
        {
            throw new InvalidOperationException($"Ya existe un panel con code='{cmd.Code}' en este alcance.");
        }
        var panel = LabPanel.Create(cmd.Code, cmd.Name, cmd.Description, cmd.OrganizationId, active: true, timeProvider: timeProvider);
        await repo.AddPanelAsync(panel, cancellationToken);

        var effectiveDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var version = LabPanelVersion.Create(panel.Id, 1, effectiveDate, PanelVersionStatus.Active, timeProvider);
        var (items, itemDtos) = await BuildItemsAsync(repo, version.Id, cmd.TestIds, cancellationToken);
        await repo.AddVersionWithItemsAsync(version, items, cancellationToken);

        return new LabPanelDetailDto(panel.Id, panel.OrganizationId, panel.Code, panel.Name, panel.Description,
            panel.Active, version.Version, version.EffectiveDate, itemDtos);
    }

    internal static async Task<(IReadOnlyList<LabPanelVersionItem> Items, IReadOnlyList<LabPanelItemDto> Dtos)> BuildItemsAsync(
        ILabCatalogRepository repo, Guid versionId, IReadOnlyList<Guid> testIds, CancellationToken cancellationToken)
    {
        var tests = (await repo.GetTestsByIdsAsync(testIds, cancellationToken)).ToDictionary(t => t.Id);
        var items = new List<LabPanelVersionItem>();
        var dtos = new List<LabPanelItemDto>();
        var order = 0;
        foreach (var id in testIds)
        {
            if (!tests.TryGetValue(id, out var t))
            {
                throw new ArgumentException($"La prueba '{id}' no existe; no se puede agregar al panel.");
            }
            items.Add(LabPanelVersionItem.Create(versionId, id, order));
            dtos.Add(new LabPanelItemDto(t.Id, t.Code, t.Name, t.Unit, order));
            order++;
        }
        return (items, dtos);
    }
}

public sealed record CreatePanelVersionCommand(Guid PanelId, IReadOnlyList<Guid> TestIds);

public sealed class CreatePanelVersionHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabPanelDetailDto> HandleAsync(CreatePanelVersionCommand cmd, CancellationToken cancellationToken = default)
    {
        var panel = await repo.GetPanelByIdAsync(cmd.PanelId, cancellationToken)
            ?? throw new KeyNotFoundException($"Panel '{cmd.PanelId}' no existe.");

        var nextVersion = await repo.GetMaxVersionNumberAsync(panel.Id, cancellationToken) + 1;
        var effectiveDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var version = LabPanelVersion.Create(panel.Id, nextVersion, effectiveDate, PanelVersionStatus.Active, timeProvider);
        var (items, itemDtos) = await CreateLabPanelHandler.BuildItemsAsync(repo, version.Id, cmd.TestIds, cancellationToken);
        await repo.AddVersionWithItemsAsync(version, items, cancellationToken);

        return new LabPanelDetailDto(panel.Id, panel.OrganizationId, panel.Code, panel.Name, panel.Description,
            panel.Active, version.Version, version.EffectiveDate, itemDtos);
    }
}

// -------------------------- Rangos de referencia -----------------------

public sealed record CreateReferenceRangeCommand(
    Guid? OrganizationId, Guid TestId, string Sex, int? AgeMinYears, int? AgeMaxYears, bool? Pregnancy,
    string? Method, decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh, string? Unit);

public sealed class CreateReferenceRangeHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<ReferenceRangeDto> HandleAsync(CreateReferenceRangeCommand cmd, CancellationToken cancellationToken = default)
    {
        var test = await repo.GetTestByIdAsync(cmd.TestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Prueba '{cmd.TestId}' no existe.");
        var range = ReferenceRange.Create(
            test.Id, LabCatalogParsing.Sex(cmd.Sex), cmd.AgeMinYears, cmd.AgeMaxYears, cmd.Pregnancy, cmd.Method,
            cmd.Low, cmd.High, cmd.CriticalLow, cmd.CriticalHigh, cmd.Unit, cmd.OrganizationId, timeProvider);
        await repo.AddRangeAsync(range, cancellationToken);
        return ReferenceRangeDto.FromDomain(range);
    }
}

public sealed record UpdateReferenceRangeCommand(
    Guid Id, string Sex, int? AgeMinYears, int? AgeMaxYears, bool? Pregnancy, string? Method,
    decimal? Low, decimal? High, decimal? CriticalLow, decimal? CriticalHigh, string? Unit);

public sealed class UpdateReferenceRangeHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<ReferenceRangeDto> HandleAsync(UpdateReferenceRangeCommand cmd, CancellationToken cancellationToken = default)
    {
        var range = await repo.GetRangeByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Rango '{cmd.Id}' no existe.");
        range.Edit(
            LabCatalogParsing.Sex(cmd.Sex), cmd.AgeMinYears, cmd.AgeMaxYears, cmd.Pregnancy, cmd.Method,
            cmd.Low, cmd.High, cmd.CriticalLow, cmd.CriticalHigh, cmd.Unit, timeProvider);
        await repo.UpdateRangeAsync(range, cancellationToken);
        return ReferenceRangeDto.FromDomain(range);
    }
}

public sealed record DeleteReferenceRangeCommand(Guid Id);

public sealed class DeleteReferenceRangeHandler(ILabCatalogRepository repo)
{
    public async Task HandleAsync(DeleteReferenceRangeCommand cmd, CancellationToken cancellationToken = default)
    {
        var range = await repo.GetRangeByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"Rango '{cmd.Id}' no existe.");
        await repo.DeleteRangeAsync(range, cancellationToken);
    }
}

// ------------------------- Ofertas por sede ----------------------------

public sealed record SetOfferingCommand(Guid OrganizationId, Guid TestId, bool Offered);

public sealed class SetOfferingHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task HandleAsync(SetOfferingCommand cmd, CancellationToken cancellationToken = default)
    {
        var test = await repo.GetTestByIdAsync(cmd.TestId, cancellationToken)
            ?? throw new KeyNotFoundException($"Prueba '{cmd.TestId}' no existe.");
        var offering = LabTestOffering.Create(cmd.OrganizationId, test.Id, cmd.Offered, timeProvider);
        await repo.UpsertOfferingAsync(offering, cancellationToken);
    }
}
