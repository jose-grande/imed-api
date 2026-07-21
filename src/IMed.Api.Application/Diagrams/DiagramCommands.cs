using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Diagrams;

namespace IMed.Api.Application.Diagrams;

public sealed record CreateDiagramCommand(
    string Code,
    string Title,
    string? Description,
    string? SpecialtyCode,
    string Version,
    string SvgContent,
    JsonElement ZonesJson,
    JsonElement? AnnotationStyleJson,
    JsonElement? LinkedFormCodesJson,
    JsonElement? ZoneBodySiteMapJson,
    Guid? OrganizationId,
    Guid CreatedBy);

public sealed class CreateDiagramHandler(
    IDiagramDefinitionRepository repo,
    TimeProvider timeProvider)
{
    public async Task<DiagramDefinitionDto> HandleAsync(CreateDiagramCommand cmd, CancellationToken cancellationToken = default)
    {
        if (await repo.ExistsAsync(cmd.Code, cmd.Version, cmd.OrganizationId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Ya existe una DiagramDefinition con code='{cmd.Code}' y version='{cmd.Version}' en esta organización.");
        }

        var diagram = DiagramDefinition.CreateDraft(
            code: cmd.Code,
            title: cmd.Title,
            svgContent: cmd.SvgContent,
            zonesJson: cmd.ZonesJson.GetRawText(),
            createdBy: cmd.CreatedBy,
            specialtyCode: cmd.SpecialtyCode,
            description: cmd.Description,
            annotationStyleJson: cmd.AnnotationStyleJson?.GetRawText(),
            linkedFormCodesJson: cmd.LinkedFormCodesJson?.GetRawText(),
            zoneBodySiteMapJson: cmd.ZoneBodySiteMapJson?.GetRawText(),
            organizationId: cmd.OrganizationId,
            version: cmd.Version,
            timeProvider: timeProvider);

        await repo.AddAsync(diagram, cancellationToken);
        return DiagramDefinitionDto.FromDomain(diagram);
    }
}

public sealed record UpdateDiagramCommand(
    Guid Id,
    string Title,
    string? Description,
    string? SpecialtyCode,
    string SvgContent,
    JsonElement ZonesJson,
    JsonElement? AnnotationStyleJson,
    JsonElement? LinkedFormCodesJson,
    JsonElement? ZoneBodySiteMapJson);

public sealed class UpdateDiagramHandler(IDiagramDefinitionRepository repo)
{
    public async Task<DiagramDefinitionDto> HandleAsync(UpdateDiagramCommand cmd, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"DiagramDefinition '{cmd.Id}' no existe.");

        diagram.EditDraft(
            title: cmd.Title,
            svgContent: cmd.SvgContent,
            zonesJson: cmd.ZonesJson.GetRawText(),
            description: cmd.Description,
            specialtyCode: cmd.SpecialtyCode,
            annotationStyleJson: cmd.AnnotationStyleJson?.GetRawText(),
            linkedFormCodesJson: cmd.LinkedFormCodesJson?.GetRawText(),
            zoneBodySiteMapJson: cmd.ZoneBodySiteMapJson?.GetRawText());

        await repo.UpdateAsync(diagram, cancellationToken);
        return DiagramDefinitionDto.FromDomain(diagram);
    }
}

public sealed record PublishDiagramCommand(Guid Id, Guid PublishedBy);

public sealed class PublishDiagramHandler(IDiagramDefinitionRepository repo, TimeProvider timeProvider)
{
    public async Task<DiagramDefinitionDto> HandleAsync(PublishDiagramCommand cmd, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"DiagramDefinition '{cmd.Id}' no existe.");
        diagram.Publish(cmd.PublishedBy, timeProvider);
        await repo.UpdateAsync(diagram, cancellationToken);
        return DiagramDefinitionDto.FromDomain(diagram);
    }
}

public sealed record RetireDiagramCommand(Guid Id);

public sealed class RetireDiagramHandler(IDiagramDefinitionRepository repo)
{
    public async Task<DiagramDefinitionDto> HandleAsync(RetireDiagramCommand cmd, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"DiagramDefinition '{cmd.Id}' no existe.");
        diagram.Retire();
        await repo.UpdateAsync(diagram, cancellationToken);
        return DiagramDefinitionDto.FromDomain(diagram);
    }
}

public sealed record DeleteDiagramCommand(Guid Id);

public sealed class DeleteDiagramHandler(IDiagramDefinitionRepository repo)
{
    public async Task HandleAsync(DeleteDiagramCommand cmd, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"DiagramDefinition '{cmd.Id}' no existe.");
        if (diagram.Status != DiagramStatus.Draft)
        {
            throw new InvalidOperationException(
                "Solo se pueden borrar DiagramDefinitions en Draft. Las Published se retiran con /retire.");
        }
        await repo.DeleteAsync(cmd.Id, cancellationToken);
    }
}
