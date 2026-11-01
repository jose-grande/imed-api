namespace IMed.Api.Domain.Forms;

/// <summary>
/// Un campo de un catálogo del studio: su widget, lo que una organización puede cambiar de él y su
/// mapeo a FHIR. Una fila por campo, para poder ajustar o desactivar uno sin tocar los demás.
///
/// <para>Las columnas estructuradas (clave, widget, banderas, orden) son las que se consultan; el resto
/// (propiedades por defecto, lista de opciones, mapeo) es JSON porque cada campo lo tiene anidado y
/// con forma propia. Se cargan con los scripts de <c>db/forms</c>; la API solo los lee.</para>
/// </summary>
public sealed class FormCatalogField
{
    public Guid Id { get; private set; }

    public Guid CatalogId { get; private set; }

    /// <summary>Id estable del campo. Es el <c>linkId</c> del mapeo y el <c>name</c> del campo en el formulario.</summary>
    public string FieldKey { get; private set; } = string.Empty;

    /// <summary>Posición del campo en el catálogo (orden en que se ofrece al diseñar).</summary>
    public int SortOrder { get; private set; }

    /// <summary>Control con el que se dibuja: <c>text</c>, <c>textarea</c>, <c>select</c>, <c>date</c> o <c>toggle</c>.</summary>
    public string Widget { get; private set; } = string.Empty;

    /// <summary>Los campos núcleo no se pueden quitar de un formulario.</summary>
    public bool Core { get; private set; }

    /// <summary>Plantilla de campo personalizado: cada uso lleva su propio id y se guarda como extensión.</summary>
    public bool Custom { get; private set; }

    /// <summary>Agrega la regla de formato de correo.</summary>
    public bool Email { get; private set; }

    /// <summary>Un campo inactivo deja de ofrecerse; los formularios ya publicados conservan su mapeo.</summary>
    public bool Active { get; private set; }

    /// <summary>Arreglo JSON con las propiedades que la organización puede cambiar.</summary>
    public string Editable { get; private set; } = "[]";

    /// <summary>Objeto JSON con las propiedades por defecto (etiqueta, texto de ejemplo, límites...).</summary>
    public string Defaults { get; private set; } = "{}";

    /// <summary>Objeto JSON con los mensajes de las reglas de validación, si el campo las tiene.</summary>
    public string? Messages { get; private set; }

    /// <summary>Origen de las opciones de un select (ValueSet, lista escrita o consulta a una API), si lo es.</summary>
    public string? ListSource { get; private set; }

    /// <summary>Elemento de mapeo (sin <c>linkId</c>): <c>mapsTo</c>, <c>extractor</c> y sus datos.</summary>
    public string Binding { get; private set; } = "{}";

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private FormCatalogField() { }

    public static FormCatalogField Create(
        Guid catalogId,
        string fieldKey,
        int sortOrder,
        string widget,
        string editable,
        string defaults,
        string binding,
        bool core = false,
        bool custom = false,
        bool email = false,
        bool active = true,
        string? messages = null,
        string? listSource = null,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(fieldKey))
        {
            throw new ArgumentException("FieldKey es obligatorio.", nameof(fieldKey));
        }
        if (string.IsNullOrWhiteSpace(widget))
        {
            throw new ArgumentException("Widget es obligatorio.", nameof(widget));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FormCatalogField
        {
            Id = Guid.CreateVersion7(now),
            CatalogId = catalogId,
            FieldKey = fieldKey,
            SortOrder = sortOrder,
            Widget = widget,
            Core = core,
            Custom = custom,
            Email = email,
            Active = active,
            Editable = editable,
            Defaults = defaults,
            Messages = messages,
            ListSource = listSource,
            Binding = binding,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
