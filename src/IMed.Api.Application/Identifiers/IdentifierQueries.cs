using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Identifiers;

/// <summary>DTO del endpoint <c>GET /api/identifiers/types?country=HN</c>.</summary>
public sealed record IdentifierTypeDto(
    string Code,
    string Label,
    string FhirSystemUri,
    string Regex,
    string Hl7V2Code,
    string? NormalizationHint);

public sealed record ListIdentifierTypesQuery(string CountryCode);

public sealed class ListIdentifierTypesHandler(IIdentifierTypeRegistry registry)
{
    public IReadOnlyList<IdentifierTypeDto> Handle(ListIdentifierTypesQuery query)
    {
        if (string.IsNullOrWhiteSpace(query.CountryCode))
        {
            return Array.Empty<IdentifierTypeDto>();
        }

        return registry.ForCountry(query.CountryCode)
            .Select(t => new IdentifierTypeDto(
                Code: t.Code,
                Label: t.Label,
                FhirSystemUri: t.FhirSystemUri,
                Regex: t.Regex,
                Hl7V2Code: t.Hl7V2Code,
                NormalizationHint: t.NormalizationHint))
            .ToList();
    }
}

/// <summary>Request body de <c>POST /api/identifiers/validate</c>.</summary>
public sealed record ValidateIdentifierCommand(string CountryCode, string TypeCode, string Value);

public sealed class ValidateIdentifierHandler(IIdentifierValidator validator)
{
    public IdentifierValidationResult Handle(ValidateIdentifierCommand command)
    {
        return validator.Validate(command.CountryCode, command.TypeCode, command.Value);
    }
}
