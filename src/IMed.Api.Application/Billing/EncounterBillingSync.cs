using System.Text.Json;
using System.Text.Json.Nodes;
using IMed.Api.Application.FhirGateway;
using IMed.Api.Domain.Authorization;

namespace IMed.Api.Application.Billing;

/// <summary>
/// Evento outbox que pide reconciliar el cargo de una consulta (A4). Lo encola el proxy FHIR
/// después de que HAPI aceptó una escritura de <c>Encounter</c>; lo despacha
/// <see cref="EncounterChargeReconciler"/>.
///
/// <para><b>Por qué outbox y no en el request.</b> El encuentro lo escribe el navegador a través
/// del proxy, que reenvía en streaming: cobrar dentro de ese request lo frenaría y, si fallara,
/// dejaría al médico con un error que no es suyo. El outbox reintenta hasta que el cargo quede.</para>
/// </summary>
public static class EncounterBillingSyncEvent
{
    public const string OutboxEventType = "encounter.billing-sync";

    public sealed record Payload(Guid OrganizationId, string EncounterId);

    /// <summary>Los encuentros que escribió un request ya aceptado por HAPI.</summary>
    public static IReadOnlyList<string> WrittenEncounterIds(
        FhirRequestDescriptor descriptor, string? body, string? locationHeader) =>
        FhirWrites.WrittenIds("Encounter", descriptor, body, locationHeader);

}

/// <summary>Qué recursos escribió un request FHIR ya aceptado por HAPI.</summary>
public static class FhirWrites
{
    /// <summary>
    /// Ids del tipo pedido que escribió el request: el de la ruta en un <c>PUT</c>, el de la
    /// cabecera <c>Location</c> en un <c>POST</c>, y las entradas <c>PUT Tipo/{id}</c> de un Bundle
    /// de transacción. Los <c>POST</c> dentro de un Bundle no traen id en el cuerpo y no se ven:
    /// quien crea esos recursos por Bundle tiene que encolar por su cuenta.
    /// </summary>
    public static IReadOnlyList<string> WrittenIds(
        string resourceType, FhirRequestDescriptor descriptor, string? body, string? locationHeader)
    {
        if (descriptor.ResourceType == resourceType)
        {
            return descriptor.Verb switch
            {
                FhirVerb.Update when descriptor.ResourceId is { Length: > 0 } id => [id],
                FhirVerb.Create when IdFromLocation(resourceType, locationHeader) is { } id => [id],
                _ => [],
            };
        }

        if (descriptor.IsBundle && !string.IsNullOrWhiteSpace(body))
        {
            var prefijo = resourceType + "/";
            try
            {
                return (JsonNode.Parse(body)?["entry"]?.AsArray() ?? [])
                    .Select(e => e?["request"]?["url"]?.GetValue<string>())
                    .Where(u => u is not null && u.StartsWith(prefijo, StringComparison.Ordinal))
                    .Select(u => u![prefijo.Length..].Split('?', '/')[0])
                    .Where(id => id.Length > 0)
                    .Distinct()
                    .ToList();
            }
            catch (JsonException)
            {
                return [];
            }
        }
        return [];
    }

    /// <summary><c>…/Encounter/123/_history/1</c> → <c>123</c>.</summary>
    public static string? IdFromLocation(string resourceType, string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }
        var partes = location.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.LastIndexOf(partes, resourceType);
        return i >= 0 && i + 1 < partes.Length ? partes[i + 1] : null;
    }
}
