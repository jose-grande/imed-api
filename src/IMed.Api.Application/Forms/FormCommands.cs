using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Forms;

namespace IMed.Api.Application.Forms;

public sealed record CreateFormCommand(
    string Code,
    string Title,
    string? Description,
    string? SpecialtyCode,
    string Version,
    JsonElement SchemaJson,
    JsonElement FhirMappingJson,
    Guid? OrganizationId,
    Guid CreatedBy);

public sealed class CreateFormHandler(
    IFormDefinitionRepository repo,
    TimeProvider timeProvider)
{
    public async Task<FormDefinitionDto> HandleAsync(CreateFormCommand cmd, CancellationToken cancellationToken = default)
    {
        if (await repo.ExistsAsync(cmd.Code, cmd.Version, cmd.OrganizationId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Ya existe una FormDefinition con code='{cmd.Code}' y version='{cmd.Version}' en esta organización.");
        }

        var form = FormDefinition.CreateDraft(
            code: cmd.Code,
            title: cmd.Title,
            schemaJson: cmd.SchemaJson.GetRawText(),
            fhirMappingJson: cmd.FhirMappingJson.GetRawText(),
            createdBy: cmd.CreatedBy,
            specialtyCode: cmd.SpecialtyCode,
            description: cmd.Description,
            organizationId: cmd.OrganizationId,
            version: cmd.Version,
            timeProvider: timeProvider);

        await repo.AddAsync(form, cancellationToken);
        return FormDefinitionDto.FromDomain(form);
    }
}

public sealed record UpdateFormCommand(
    Guid Id,
    string Title,
    string? Description,
    string? SpecialtyCode,
    JsonElement SchemaJson,
    JsonElement FhirMappingJson);

public sealed class UpdateFormHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto> HandleAsync(UpdateFormCommand cmd, CancellationToken cancellationToken = default)
    {
        var form = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"FormDefinition '{cmd.Id}' no existe.");

        form.EditDraft(
            title: cmd.Title,
            schemaJson: cmd.SchemaJson.GetRawText(),
            fhirMappingJson: cmd.FhirMappingJson.GetRawText(),
            description: cmd.Description,
            specialtyCode: cmd.SpecialtyCode);

        await repo.UpdateAsync(form, cancellationToken);
        return FormDefinitionDto.FromDomain(form);
    }
}

public sealed record PublishFormCommand(Guid Id, Guid PublishedBy);

public sealed class PublishFormHandler(IFormDefinitionRepository repo, TimeProvider timeProvider)
{
    public async Task<FormDefinitionDto> HandleAsync(PublishFormCommand cmd, CancellationToken cancellationToken = default)
    {
        var form = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"FormDefinition '{cmd.Id}' no existe.");
        form.Publish(cmd.PublishedBy, timeProvider);
        await repo.UpdateAsync(form, cancellationToken);
        return FormDefinitionDto.FromDomain(form);
    }
}

public sealed record RetireFormCommand(Guid Id);

public sealed class RetireFormHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto> HandleAsync(RetireFormCommand cmd, CancellationToken cancellationToken = default)
    {
        var form = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"FormDefinition '{cmd.Id}' no existe.");
        form.Retire();
        await repo.UpdateAsync(form, cancellationToken);
        return FormDefinitionDto.FromDomain(form);
    }
}

public sealed record DeleteFormCommand(Guid Id);

public sealed class DeleteFormHandler(IFormDefinitionRepository repo)
{
    public async Task HandleAsync(DeleteFormCommand cmd, CancellationToken cancellationToken = default)
    {
        var form = await repo.GetByIdAsync(cmd.Id, cancellationToken)
            ?? throw new KeyNotFoundException($"FormDefinition '{cmd.Id}' no existe.");
        if (form.Status != FormStatus.Draft)
        {
            throw new InvalidOperationException(
                "Solo se pueden borrar FormDefinitions en Draft. Las Published se retiran con /retire.");
        }
        await repo.DeleteAsync(cmd.Id, cancellationToken);
    }
}
