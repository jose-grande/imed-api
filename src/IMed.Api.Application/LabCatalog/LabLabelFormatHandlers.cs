using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Catalogs.Lab;

namespace IMed.Api.Application.LabCatalog;

// ---------------------------------- DTOs ----------------------------------

/// <summary>
/// Formato físico de etiqueta configurado por el laboratorio (R6-F4b).
/// <paramref name="BarcodeModuleMm"/> se calcula (no se persiste) para que la UI pueda
/// avisar cuándo el tamaño elegido deja el código de barras por debajo de lo legible.
/// </summary>
public sealed record LabLabelFormatDto(
    Guid Id,
    Guid? OrganizationId,
    string Code,
    string Name,
    decimal WidthMm,
    decimal HeightMm,
    decimal MarginMm,
    decimal OffsetXMm,
    decimal OffsetYMm,
    bool IsDefault,
    int SortOrder,
    bool Active,
    decimal BarcodeModuleMm)
{
    /// <summary>
    /// Ancho de módulo estimado para un accession típico (~18 caracteres) con las zonas
    /// mudas de 10 módulos a cada lado. La especificación Code128 considera 0.19 mm el
    /// mínimo práctico para lectores de mano; por debajo, el barcode se imprime pero
    /// muchos escáneres fallan y el técnico termina tecleando el accession.
    /// </summary>
    public const decimal MinReadableModuleMm = 0.19m;

    /// <summary>Módulos que ocupa un accession típico + zonas mudas (estimación de UI).</summary>
    private const decimal TypicalBarcodeModules = 200m;

    public static LabLabelFormatDto FromDomain(LabLabelFormat f)
    {
        var usableWidth = f.WidthMm - (f.MarginMm * 2);
        var moduleMm = usableWidth <= 0 ? 0m : Math.Round(usableWidth / TypicalBarcodeModules, 3);
        return new LabLabelFormatDto(
            f.Id, f.OrganizationId, f.Code, f.Name, f.WidthMm, f.HeightMm, f.MarginMm,
            f.OffsetXMm, f.OffsetYMm, f.IsDefault, f.SortOrder, f.Active, moduleMm);
    }
}

/// <summary>Asignación de un tubo o tipo de muestra a un formato de etiqueta (R6-F4b).</summary>
public sealed record LabLabelFormatAssignmentDto(
    Guid Id,
    string? TubeTypeCode,
    string? SpecimenTypeCode,
    Guid LabelFormatId)
{
    public static LabLabelFormatAssignmentDto FromDomain(LabLabelFormatAssignment a) =>
        new(a.Id, a.TubeTypeCode, a.SpecimenTypeCode, a.LabLabelFormatId);
}

// --------------------------------- Queries ---------------------------------

public sealed record ListLabelFormatsQuery(Guid? OrganizationId, bool IncludeInactive = false);

public sealed class ListLabelFormatsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<LabLabelFormatDto>> HandleAsync(
        ListLabelFormatsQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListLabelFormatsAsync(query.OrganizationId, query.IncludeInactive, cancellationToken);
        return rows.Select(LabLabelFormatDto.FromDomain).ToList();
    }
}

public sealed record ListLabelFormatAssignmentsQuery(Guid? OrganizationId);

public sealed class ListLabelFormatAssignmentsHandler(ILabCatalogRepository repo)
{
    public async Task<IReadOnlyList<LabLabelFormatAssignmentDto>> HandleAsync(
        ListLabelFormatAssignmentsQuery query, CancellationToken cancellationToken = default)
    {
        var rows = await repo.ListLabelFormatAssignmentsAsync(query.OrganizationId, cancellationToken);
        return rows.Select(LabLabelFormatAssignmentDto.FromDomain).ToList();
    }
}

// -------------------------------- Commands ---------------------------------

public sealed record CreateLabelFormatCommand(
    Guid OrganizationId,
    string Code,
    string Name,
    decimal WidthMm,
    decimal HeightMm,
    decimal MarginMm,
    decimal OffsetXMm,
    decimal OffsetYMm,
    bool IsDefault,
    int SortOrder);

public sealed class CreateLabelFormatHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabLabelFormatDto> HandleAsync(
        CreateLabelFormatCommand command, CancellationToken cancellationToken = default)
    {
        var format = LabLabelFormat.Create(
            command.Code, command.Name, command.WidthMm, command.HeightMm, command.MarginMm,
            command.OffsetXMm, command.OffsetYMm, command.IsDefault, command.SortOrder,
            command.OrganizationId, active: true, timeProvider);

        await repo.AddLabelFormatAsync(format, cancellationToken);
        if (command.IsDefault)
        {
            await repo.ClearDefaultLabelFormatAsync(command.OrganizationId, format.Id, cancellationToken);
        }
        return LabLabelFormatDto.FromDomain(format);
    }
}

public sealed record UpdateLabelFormatCommand(
    Guid OrganizationId,
    Guid Id,
    string Name,
    decimal WidthMm,
    decimal HeightMm,
    decimal MarginMm,
    decimal OffsetXMm,
    decimal OffsetYMm,
    bool IsDefault,
    int SortOrder,
    bool Active);

public sealed class UpdateLabelFormatHandler(ILabCatalogRepository repo, TimeProvider timeProvider)
{
    public async Task<LabLabelFormatDto?> HandleAsync(
        UpdateLabelFormatCommand command, CancellationToken cancellationToken = default)
    {
        var format = await repo.GetLabelFormatByIdAsync(command.OrganizationId, command.Id, cancellationToken);
        if (format is null)
        {
            return null;
        }

        format.Edit(
            command.Name, command.WidthMm, command.HeightMm, command.MarginMm,
            command.OffsetXMm, command.OffsetYMm, command.SortOrder, timeProvider);
        format.SetDefault(command.IsDefault, timeProvider);
        format.SetActive(command.Active, timeProvider);

        await repo.UpdateLabelFormatAsync(format, cancellationToken);
        if (command.IsDefault)
        {
            await repo.ClearDefaultLabelFormatAsync(command.OrganizationId, format.Id, cancellationToken);
        }
        return LabLabelFormatDto.FromDomain(format);
    }
}

/// <summary>
/// Asigna (o desasigna, con <c>LabelFormatId = null</c>) el formato de un tubo o tipo de
/// muestra. Desasignar devuelve ese recipiente al formato default de la organización.
/// </summary>
public sealed record SetLabelFormatAssignmentCommand(
    Guid OrganizationId,
    string? TubeTypeCode,
    string? SpecimenTypeCode,
    Guid? LabelFormatId);

public sealed class SetLabelFormatAssignmentHandler(ILabCatalogRepository repo)
{
    public async Task<bool> HandleAsync(
        SetLabelFormatAssignmentCommand command, CancellationToken cancellationToken = default)
    {
        var hasTube = !string.IsNullOrWhiteSpace(command.TubeTypeCode);
        var hasSpecimen = !string.IsNullOrWhiteSpace(command.SpecimenTypeCode);
        if (hasTube == hasSpecimen)
        {
            throw new ArgumentException(
                "Indicá exactamente uno: código de tubo o código de tipo de muestra.", nameof(command));
        }

        var tube = hasTube ? command.TubeTypeCode!.Trim() : null;
        var specimen = hasSpecimen ? command.SpecimenTypeCode!.Trim() : null;

        if (command.LabelFormatId is not { } formatId)
        {
            await repo.DeleteLabelFormatAssignmentAsync(command.OrganizationId, tube, specimen, cancellationToken);
            return true;
        }

        // El formato debe ser de la org: sin este chequeo se podría apuntar a la plantilla
        // global (o a la de otro tenant) pasando un id arbitrario.
        var format = await repo.GetLabelFormatByIdAsync(command.OrganizationId, formatId, cancellationToken);
        if (format is null)
        {
            return false;
        }

        await repo.UpsertLabelFormatAssignmentAsync(
            command.OrganizationId, tube, specimen, formatId, cancellationToken);
        return true;
    }
}
