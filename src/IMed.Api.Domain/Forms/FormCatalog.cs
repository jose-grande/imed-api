namespace IMed.Api.Domain.Forms;

/// <summary>
/// Cabecera del catálogo de campos de un tipo de formulario del studio (por ejemplo
/// <c>patient-registration</c>). Lo define la plataforma: las organizaciones componen sus formularios
/// con los campos del catálogo, cada uno con su widget, las propiedades que se pueden cambiar y su
/// mapeo a FHIR.
///
/// <para>Una fila por (tipo de formulario, versión). Los campos están en <see cref="FormCatalogField"/>,
/// una fila por campo. Se cargan con los scripts de <c>db/forms</c>; la API solo los lee.</para>
/// </summary>
public sealed class FormCatalog
{
    public Guid Id { get; private set; }

    /// <summary>Tipo de formulario; coincide con <c>FormDefinition.Code</c>.</summary>
    public string FormCode { get; private set; } = string.Empty;

    /// <summary>Versión del catálogo (SemVer). Se usa la más alta de las activas.</summary>
    public string Version { get; private set; } = "1.0.0";

    public string Title { get; private set; } = string.Empty;

    /// <summary>Arreglo JSON con los recursos FHIR que mapea el formulario: <c>["Patient","RelatedPerson"]</c>.</summary>
    public string ResourceTypes { get; private set; } = "[]";

    public bool Active { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private FormCatalog() { }

    public static FormCatalog Create(
        string formCode,
        string version,
        string title,
        string resourceTypes,
        bool active = true,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(formCode))
        {
            throw new ArgumentException("FormCode es obligatorio.", nameof(formCode));
        }
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new ArgumentException("Version es obligatoria (SemVer, ej. '1.0.0').", nameof(version));
        }
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Title es obligatorio.", nameof(title));
        }
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FormCatalog
        {
            Id = Guid.CreateVersion7(now),
            FormCode = formCode,
            Version = version,
            Title = title,
            ResourceTypes = string.IsNullOrWhiteSpace(resourceTypes) ? "[]" : resourceTypes,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
