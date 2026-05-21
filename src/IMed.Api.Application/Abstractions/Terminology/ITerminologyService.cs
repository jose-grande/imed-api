namespace IMed.Api.Application.Abstractions.Terminology;

/// <summary>
/// Port que abstrae el servicio de terminología. Los consumidores (handlers,
/// endpoints, EncounterCloseFlow) hablan con esta interfaz; nunca con el
/// adapter concreto.
///
/// Diseñado sobre las operaciones FHIR estándar para que cualquier provider
/// que las exponga (HAPI Terminology Server, Snowstorm, Ontoserver, tx.fhir.org)
/// se pueda integrar como adapter delgado sin cambiar consumers:
/// - <see cref="SearchAsync"/> ↔ <c>ValueSet/$expand</c>
/// - <see cref="LookupAsync"/> ↔ <c>CodeSystem/$lookup</c>
/// - <see cref="ValidateCodeAsync"/> ↔ <c>CodeSystem/$validate-code</c>
///
/// El adapter activo se decide por configuración (<c>Terminology:Provider</c>).
/// </summary>
public interface ITerminologyService
{
    /// <summary>
    /// Búsqueda typeahead. Devuelve conceptos que matchean el término en el
    /// CodeSystem indicado, ordenados por relevancia.
    /// </summary>
    /// <exception cref="UnsupportedCodeSystemException">
    /// Si no hay reader registrado para <c>request.System</c>.
    /// </exception>
    Task<TerminologySearchResult> SearchAsync(
        TerminologySearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lookup directo de un código. Devuelve null si el código no existe en
    /// el CodeSystem. Usado para denormalización (ej: hidratar el display
    /// al persistir un Condition.code).
    /// </summary>
    /// <exception cref="UnsupportedCodeSystemException">
    /// Si no hay reader registrado para <paramref name="system"/>.
    /// </exception>
    Task<TerminologyConcept?> LookupAsync(
        string system,
        string code,
        string? language = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verificación liviana de existencia. Usar cuando solo necesitamos
    /// validar que un código pertenece al CodeSystem (ej: validation pipeline
    /// antes de aceptar un Condition).
    /// </summary>
    /// <exception cref="UnsupportedCodeSystemException">
    /// Si no hay reader registrado para <paramref name="system"/>.
    /// </exception>
    Task<bool> ValidateCodeAsync(
        string system,
        string code,
        CancellationToken cancellationToken = default);
}
