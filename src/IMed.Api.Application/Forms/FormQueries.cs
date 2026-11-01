using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Forms;

public sealed record GetFormByIdQuery(Guid Id);

public sealed class GetFormByIdHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto?> HandleAsync(GetFormByIdQuery query, CancellationToken cancellationToken = default)
    {
        var form = await repo.GetByIdAsync(query.Id, cancellationToken);
        return form is null ? null : FormDefinitionDto.FromDomain(form);
    }
}

public sealed record ListFormsQuery(Guid? OrganizationId, bool IncludeNonPublished);

public sealed class ListFormsHandler(IFormDefinitionRepository repo)
{
    public async Task<IReadOnlyList<FormDefinitionDto>> HandleAsync(ListFormsQuery query, CancellationToken cancellationToken = default)
    {
        var forms = await repo.ListAsync(query.OrganizationId, query.IncludeNonPublished, cancellationToken);
        return forms.Select(FormDefinitionDto.FromDomain).ToList();
    }
}

public sealed record GetFormBySpecialtyQuery(Guid OrganizationId, string? SpecialtyCode);

public sealed class GetFormBySpecialtyHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto?> HandleAsync(GetFormBySpecialtyQuery query, CancellationToken cancellationToken = default)
    {
        var form = await repo.ResolveBySpecialtyAsync(query.OrganizationId, query.SpecialtyCode, cancellationToken);
        return form is null ? null : FormDefinitionDto.FromDomain(form);
    }
}

public sealed record GetFormByCodeQuery(Guid OrganizationId, string Code, string Version);

public sealed class GetFormByCodeHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto?> HandleAsync(GetFormByCodeQuery query, CancellationToken cancellationToken = default)
    {
        var form = await repo.ResolveByCodeAndVersionAsync(query.OrganizationId, query.Code, query.Version, cancellationToken);
        return form is null ? null : FormDefinitionDto.FromDomain(form);
    }
}

public sealed record ResolveFormByCodeQuery(Guid OrganizationId, string Code);

public sealed class ResolveFormByCodeHandler(IFormDefinitionRepository repo)
{
    public async Task<FormDefinitionDto?> HandleAsync(ResolveFormByCodeQuery query, CancellationToken cancellationToken = default)
    {
        var form = await repo.ResolveLatestByCodeAsync(query.OrganizationId, query.Code, cancellationToken);
        return form is null ? null : FormDefinitionDto.FromDomain(form);
    }
}
