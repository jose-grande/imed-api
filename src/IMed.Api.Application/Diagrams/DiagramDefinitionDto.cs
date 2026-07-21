using System.Text.Json;
using IMed.Api.Domain.Diagrams;

namespace IMed.Api.Application.Diagrams;

/// <summary>DTO de salida — lo que el frontend recibe al hacer GET /api/diagrams/...</summary>
public sealed record DiagramDefinitionDto(
    Guid Id,
    string Code,
    string? SpecialtyCode,
    string Version,
    string Status,
    string Title,
    string? Description,
    string SvgContent,
    JsonDocument ZonesJson,
    JsonDocument AnnotationStyleJson,
    JsonDocument LinkedFormCodesJson,
    JsonDocument ZoneBodySiteMapJson,
    Guid? OrganizationId,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy)
{
    public static DiagramDefinitionDto FromDomain(DiagramDefinition d) => new(
        Id: d.Id,
        Code: d.Code,
        SpecialtyCode: d.SpecialtyCode,
        Version: d.Version,
        Status: d.Status.ToString().ToLowerInvariant(),
        Title: d.Title,
        Description: d.Description,
        SvgContent: d.SvgContent,
        ZonesJson: JsonDocument.Parse(d.ZonesJson),
        AnnotationStyleJson: JsonDocument.Parse(d.AnnotationStyleJson),
        LinkedFormCodesJson: JsonDocument.Parse(d.LinkedFormCodesJson),
        ZoneBodySiteMapJson: JsonDocument.Parse(d.ZoneBodySiteMapJson),
        OrganizationId: d.OrganizationId,
        CreatedAt: d.CreatedAt,
        CreatedBy: d.CreatedBy,
        PublishedAt: d.PublishedAt,
        PublishedBy: d.PublishedBy);
}
