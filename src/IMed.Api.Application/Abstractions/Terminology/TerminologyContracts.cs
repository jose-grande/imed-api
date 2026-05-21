namespace IMed.Api.Application.Abstractions.Terminology;

/// <summary>
/// Petición de búsqueda typeahead contra un CodeSystem. Mapea conceptualmente
/// a la operación FHIR <c>ValueSet/$expand?url={system}&amp;filter={term}&amp;count={limit}</c>.
/// </summary>
public sealed record TerminologySearchRequest
{
    /// <summary>URI canónico del CodeSystem (ver <c>Domain.Terminology.CodeSystems</c>).</summary>
    public required string System { get; init; }

    /// <summary>Término de búsqueda. Los handlers exigen mínimo 2 caracteres.</summary>
    public required string Term { get; init; }

    /// <summary>Máximo de resultados a devolver. Los handlers lo clampean a [1, 50].</summary>
    public int Limit { get; init; } = 20;

    /// <summary>Idioma preferido para el <c>Display</c> primario. Default "es".</summary>
    public string? Language { get; init; } = "es";

    /// <summary>Si true, sólo conceptos activos del catálogo. Default true.</summary>
    public bool ActiveOnly { get; init; } = true;
}

/// <summary>
/// Resultado de búsqueda. Incluye metadata de observabilidad para diferenciar
/// resultados servidos por adapters distintos cuando coexistan providers.
/// </summary>
public sealed record TerminologySearchResult
{
    public required IReadOnlyList<TerminologyConcept> Concepts { get; init; }

    /// <summary>
    /// Total de matches. Puede exceder <c>Concepts.Count</c> si fue limitado por el limit.
    /// </summary>
    public required int Total { get; init; }

    /// <summary>Identificador del adapter que sirvió la respuesta (ej: "Postgres", "HAPI").</summary>
    public required string ProviderName { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// Un concepto del catálogo. Mapea conceptualmente a un FHIR <c>CodeSystem.concept</c>
/// o entry en <c>ValueSet.expansion.contains</c>.
/// </summary>
public sealed record TerminologyConcept
{
    /// <summary>URI canónico del CodeSystem al que pertenece este concepto.</summary>
    public required string System { get; init; }

    public required string Code { get; init; }

    /// <summary>Display primario en el idioma solicitado (típicamente español para HN).</summary>
    public required string Display { get; init; }

    /// <summary>Definición textual del concepto, si el CodeSystem la provee.</summary>
    public string? Definition { get; init; }

    /// <summary>
    /// Designations alternativas — sinónimos, traducciones a otros idiomas, abreviaciones.
    /// Equivalente a <c>CodeSystem.concept.designation[]</c> en FHIR.
    /// </summary>
    public IReadOnlyList<TerminologyDesignation>? Designations { get; init; }

    /// <summary>
    /// Properties del concepto — metadata específica del CodeSystem.
    /// Para ICD-11: chapter, parent-code, is-leaf, class-kind. Para LOINC:
    /// class, scale-type, system, property, etc. Equivalente a
    /// <c>CodeSystem.concept.property[]</c> en FHIR pero serializado plano.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>
/// Designation alternativa de un concepto (sinónimo o traducción).
/// </summary>
public sealed record TerminologyDesignation
{
    /// <summary>ISO language code, ej "en", "es", "es-ES", "es-MX".</summary>
    public required string Language { get; init; }

    public required string Value { get; init; }

    /// <summary>
    /// Uso de la designation. Códigos típicos FHIR: "synonym", "fully-specified-name",
    /// "preferred-for-language". Null si el provider no lo informa.
    /// </summary>
    public string? Use { get; init; }
}
