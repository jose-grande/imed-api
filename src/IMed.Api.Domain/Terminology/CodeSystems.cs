namespace IMed.Api.Domain.Terminology;

/// <summary>
/// URIs canónicos de los CodeSystems FHIR que el sistema reconoce. Se usan como
/// identificador de provider en <c>ITerminologyService</c> y como
/// <c>system</c> en los recursos FHIR (Condition.code.coding.system, etc.).
///
/// Agregar nuevas constantes acá conforme el sistema incorpore más estándares.
/// </summary>
public static class CodeSystems
{
    /// <summary>WHO ICD-11 MMS (Mortality and Morbidity Statistics).</summary>
    public const string Icd11 = "http://id.who.int/icd11/mms";

    /// <summary>LOINC — Logical Observation Identifiers Names and Codes.</summary>
    public const string Loinc = "http://loinc.org";

    /// <summary>SNOMED CT — Systematized Nomenclature of Medicine.</summary>
    public const string SnomedCt = "http://snomed.info/sct";

    /// <summary>RxNorm — catálogo de medicamentos del NLM (NIH).</summary>
    public const string RxNorm = "http://www.nlm.nih.gov/research/umls/rxnorm";

    /// <summary>WHO ATC — Anatomical Therapeutic Chemical classification (medicamentos).</summary>
    public const string Atc = "http://www.whocc.no/atc";
}
