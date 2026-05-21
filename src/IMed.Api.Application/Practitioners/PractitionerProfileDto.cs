namespace IMed.Api.Application.Practitioners;

/// <summary>
/// Respuesta consolidada del perfil profesional: lo que está en
/// <c>user_practitioner_assignments</c> (link estable) + lo que está en HAPI
/// (datos del Practitioner). Si el user todavía no completó su perfil,
/// los campos de Practitioner vienen vacíos.
/// </summary>
public sealed record PractitionerProfileDto(
    bool Configured,
    string? PractitionerFhirId,
    string? PractitionerRoleFhirId,
    string? Family,
    IReadOnlyList<string>? Given,
    string? Prefix,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Gender,
    string? SpecialtyCode,
    string? SpecialtyDisplay,
    /// <summary>
    /// True cuando hay invitation pending detectada para este user+org pero el
    /// lazy provision (creación de Practitioner+PractitionerRole en HAPI)
    /// falló y quedó en outbox para retry. La UI muestra "Completando perfil..."
    /// y reintenta el GET hasta que <c>Configured=true</c>.
    /// </summary>
    bool LazyProvisionInProgress = false);
