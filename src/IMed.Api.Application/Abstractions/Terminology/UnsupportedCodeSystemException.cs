namespace IMed.Api.Application.Abstractions.Terminology;

/// <summary>
/// Lanzada por un <c>ITerminologyService</c> cuando el CodeSystem solicitado
/// no tiene un provider/reader registrado en la configuración actual.
/// </summary>
public sealed class UnsupportedCodeSystemException : Exception
{
    public string System { get; }

    public UnsupportedCodeSystemException(string system)
        : base($"No hay provider de terminología registrado para el CodeSystem '{system}'.")
    {
        System = system;
    }
}
