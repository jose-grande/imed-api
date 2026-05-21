using System.Text.Json;
using IMed.Api.Domain.Forms;

namespace IMed.Api.Application.Forms;

/// <summary>DTO de salida — lo que el frontend recibe al hacer GET /api/forms/...</summary>
public sealed record FormDefinitionDto(
    Guid Id,
    string Code,
    string? SpecialtyCode,
    string Version,
    string Status,
    string Title,
    string? Description,
    JsonDocument SchemaJson,
    JsonDocument FhirMappingJson,
    Guid? OrganizationId,
    DateTimeOffset CreatedAt,
    Guid CreatedBy,
    DateTimeOffset? PublishedAt,
    Guid? PublishedBy)
{
    public static FormDefinitionDto FromDomain(FormDefinition d) => new(
        Id: d.Id,
        Code: d.Code,
        SpecialtyCode: d.SpecialtyCode,
        Version: d.Version,
        Status: d.Status.ToString().ToLowerInvariant(),
        Title: d.Title,
        Description: d.Description,
        SchemaJson: JsonDocument.Parse(d.SchemaJson),
        FhirMappingJson: JsonDocument.Parse(d.FhirMappingJson),
        OrganizationId: d.OrganizationId,
        CreatedAt: d.CreatedAt,
        CreatedBy: d.CreatedBy,
        PublishedAt: d.PublishedAt,
        PublishedBy: d.PublishedBy);
}
