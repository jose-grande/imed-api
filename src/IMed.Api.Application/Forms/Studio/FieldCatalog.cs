using System.Text.Json.Nodes;

namespace IMed.Api.Application.Forms.Studio;

/// <summary>
/// Catálogo de campos de un formulario del studio. Solo se modela lo que la API necesita para
/// validar documentos y generar el mapeo; el resto del JSON (defaults, listas, mensajes) lo
/// consume el cliente y se sirve tal cual.
/// </summary>
public sealed record FieldCatalog(string Form, string Version, IReadOnlyList<CatalogField> Fields)
{
    public CatalogField? Find(string key) => Fields.FirstOrDefault(f => f.Key == key);
}

/// <param name="Key">Id estable del campo; es el <c>linkId</c> del mapeo.</param>
/// <param name="Core">Los campos núcleo no se pueden quitar de un formulario.</param>
/// <param name="Custom">Plantilla de campo personalizado: cada uso lleva su propio id.</param>
/// <param name="Editable">Propiedades que la organización puede cambiar.</param>
/// <param name="Binding">Elemento de mapeo sin <c>linkId</c>; se copia tal cual al mapeo generado.</param>
public sealed record CatalogField(
    string Key,
    bool Core,
    bool Custom,
    IReadOnlyList<string> Editable,
    JsonObject Binding);

/// <summary>Resumen de un catálogo para listar los tipos de formulario del studio.</summary>
public sealed record FormCatalogSummary(
    string FormCode,
    string Version,
    string Title,
    IReadOnlyList<string> ResourceTypes);
