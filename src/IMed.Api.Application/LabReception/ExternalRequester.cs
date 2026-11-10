using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.LabReception;

/// <summary>
/// Médico solicitante de una orden externa (LIS-DEC-10). <b>Es opcional</b>: muchos pacientes
/// llegan a hacerse exámenes por cuenta propia, sin orden médica. Obligarlo hacía que recepción
/// escribiera "N/A" o "Particular", y como el médico se reutiliza por nombre, esos textos
/// terminaban como médicos externos en FHIR y ensuciaban los reportes por solicitante.
///
/// <para>Sin nombre, la orden queda sin <c>ServiceRequest.requester</c> y el informe omite el
/// renglón "Solicitado por".</para>
/// </summary>
internal static class ExternalRequester
{
    /// <summary>
    /// Reusa el médico externo si ya existe (dedupe por nombre) o lo crea. Devuelve
    /// <c>null</c> cuando no se indicó médico.
    /// </summary>
    public static async Task<string?> ResolveAsync(
        IHapiFhirClient hapi, string partition, string? name, string? identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        var id = string.IsNullOrWhiteSpace(identifier) ? null : identifier.Trim();
        return await hapi.FindExternalPractitionerByNameAsync(trimmed, partition, ct)
            ?? await hapi.CreateExternalPractitionerAsync(new ExternalPractitionerRequest(partition, trimmed, id), ct);
    }
}
