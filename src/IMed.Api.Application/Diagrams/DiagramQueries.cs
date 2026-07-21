using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Diagrams;

public sealed record GetDiagramByIdQuery(Guid Id);

public sealed class GetDiagramByIdHandler(IDiagramDefinitionRepository repo)
{
    public async Task<DiagramDefinitionDto?> HandleAsync(GetDiagramByIdQuery query, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.GetByIdAsync(query.Id, cancellationToken);
        return diagram is null ? null : DiagramDefinitionDto.FromDomain(diagram);
    }
}

public sealed record ListDiagramsQuery(Guid? OrganizationId, bool IncludeNonPublished);

public sealed class ListDiagramsHandler(IDiagramDefinitionRepository repo)
{
    public async Task<IReadOnlyList<DiagramDefinitionDto>> HandleAsync(ListDiagramsQuery query, CancellationToken cancellationToken = default)
    {
        var diagrams = await repo.ListAsync(query.OrganizationId, query.IncludeNonPublished, cancellationToken);
        return diagrams.Select(DiagramDefinitionDto.FromDomain).ToList();
    }
}

/// <summary>
/// A diferencia de <c>GetFormBySpecialtyQuery</c> (un único form clínico activo por
/// encuentro), una especialidad puede tener VARIOS diagramas activos a la vez
/// (ej. odontograma + futuro diagrama periodontal) — por eso devuelve una lista.
/// </summary>
public sealed record GetDiagramsBySpecialtyQuery(Guid OrganizationId, string? SpecialtyCode);

public sealed class GetDiagramsBySpecialtyHandler(IDiagramDefinitionRepository repo)
{
    public async Task<IReadOnlyList<DiagramDefinitionDto>> HandleAsync(GetDiagramsBySpecialtyQuery query, CancellationToken cancellationToken = default)
    {
        var diagrams = await repo.ResolveBySpecialtyAsync(query.OrganizationId, query.SpecialtyCode, cancellationToken);
        return diagrams.Select(DiagramDefinitionDto.FromDomain).ToList();
    }
}

public sealed record GetDiagramByCodeQuery(Guid OrganizationId, string Code, string Version);

public sealed class GetDiagramByCodeHandler(IDiagramDefinitionRepository repo)
{
    public async Task<DiagramDefinitionDto?> HandleAsync(GetDiagramByCodeQuery query, CancellationToken cancellationToken = default)
    {
        var diagram = await repo.ResolveByCodeAndVersionAsync(query.OrganizationId, query.Code, query.Version, cancellationToken);
        return diagram is null ? null : DiagramDefinitionDto.FromDomain(diagram);
    }
}
