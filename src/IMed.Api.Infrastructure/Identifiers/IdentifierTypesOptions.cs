namespace IMed.Api.Infrastructure.Identifiers;

/// <summary>
/// Bind a la sección <c>IdentifierTypes</c> de <c>appsettings.json</c>. Es una
/// lista plana — cada entry trae su <c>CountryCode</c>, así soportar países
/// nuevos es agregar entries sin redeploy.
/// </summary>
public sealed class IdentifierTypesOptions
{
    public const string SectionName = "IdentifierTypes";

    public List<IdentifierTypeEntry> Types { get; init; } = new();
}

public sealed class IdentifierTypeEntry
{
    public string CountryCode { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string FhirSystemUri { get; init; } = string.Empty;
    public string Regex { get; init; } = string.Empty;
    public string Hl7V2Code { get; init; } = string.Empty;
    public string? NormalizationHint { get; init; }
}
