namespace IMed.Api.Domain.Identifiers;

/// <summary>
/// Tipo de identifier soportado para registrar Patients. Configurado en
/// <c>appsettings.json</c> sección <c>IdentifierTypes</c> (Épica H1).
///
/// <para>Un tipo es seleccionable cuando se crea un Patient en una org cuyo
/// <c>countryCode</c> matchea. El <c>regex</c> valida el formato; el
/// <c>fhirSystemUri</c> es el system del <c>Patient.identifier[].system</c>
/// que se persiste en HAPI.</para>
///
/// <para><c>NormalizationHint</c> es una etiqueta no autoritativa que el
/// validador usa para producir la forma canónica (e.g. insertar guiones en
/// un DNI HN, pasar a uppercase un pasaporte). El cliente puede mostrarla
/// como placeholder.</para>
///
/// <para><c>Hl7V2Code</c> es el código del CodeSystem
/// <c>http://terminology.hl7.org/CodeSystem/v2-0203</c> que se emite en
/// <c>Patient.identifier[].type.coding[]</c> para conformancia FHIR Core
/// (ej. <c>NI</c> = National unique individual identifier, <c>PPN</c> =
/// Passport, <c>TAX</c> = Tax ID, <c>PRC</c> = Permanent Resident Card).</para>
/// </summary>
public sealed record IdentifierType(
    string CountryCode,
    string Code,
    string Label,
    string FhirSystemUri,
    string Regex,
    string Hl7V2Code,
    string? NormalizationHint = null);
