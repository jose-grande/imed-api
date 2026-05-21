namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Resultado de validar un identifier. Si <c>Valid=true</c>, <c>Normalized</c>
/// trae la forma canónica que conviene persistir (guiones insertados, uppercase,
/// etc.). Si <c>Valid=false</c>, <c>Errors</c> describe qué falló.
/// </summary>
public sealed record IdentifierValidationResult(
    bool Valid,
    string? Normalized,
    IReadOnlyList<string> Errors);

public interface IIdentifierValidator
{
    /// <summary>
    /// Valida un par (type, value) contra el registry. Si el tipo no existe
    /// para el país indicado, devuelve inválido con el error explicativo
    /// (no lanza excepción) — la decisión de mapear a 400 vs 422 es del
    /// endpoint llamador.
    /// </summary>
    IdentifierValidationResult Validate(string countryCode, string typeCode, string value);
}
