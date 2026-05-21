using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Catalogs;

public sealed record SearchSpecialtyQuery(string Term, int Limit);

public sealed record SpecialtyResultDto(string Code, string DisplayEs, string System);

public sealed class SearchSpecialtyHandler(ISpecialtyRepository repo)
{
    public async Task<IReadOnlyList<SpecialtyResultDto>> HandleAsync(SearchSpecialtyQuery query, CancellationToken cancellationToken = default)
    {
        var term = query.Term?.Trim() ?? string.Empty;
        if (term.Length < 2)
        {
            return Array.Empty<SpecialtyResultDto>();
        }

        var limit = Math.Clamp(query.Limit, 1, 50);
        var results = await repo.SearchAsync(term, limit, cancellationToken);
        return results.Select(s => new SpecialtyResultDto(s.Code, s.DisplayEs, s.System)).ToList();
    }
}
