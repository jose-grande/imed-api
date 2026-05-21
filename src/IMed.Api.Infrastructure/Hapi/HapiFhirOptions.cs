namespace IMed.Api.Infrastructure.Hapi;

public sealed class HapiFhirOptions
{
    public const string SectionName = "HapiFhir";

    /// <summary>URL base del FHIR server, ej: <c>http://localhost:8080/fhir</c>.</summary>
    public string BaseUrl { get; init; } = string.Empty;
}
