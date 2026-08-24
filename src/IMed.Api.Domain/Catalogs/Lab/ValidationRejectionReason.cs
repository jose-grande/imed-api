namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Motivo por el que un validador devuelve un informe al técnico en vez de liberarlo.
///
/// <para><b>Por qué es catálogo y no texto libre:</b> misma razón que
/// <see cref="SpecimenRejectionReason"/> en el preanalítico — sin códigos no se puede medir
/// <i>por qué</i> se devuelven resultados, y esa métrica es la que dice si el problema está en
/// el analizador, en la transcripción o en la toma. Un campo de texto libre produce cien
/// formas de escribir "hay que repetirlo" y ninguna estadística.</para>
///
/// <para>No se confunde con el rechazo de muestra: aquel es preanalítico (la muestra no sirve);
/// este es analítico (la muestra sirvió, el resultado no convence).</para>
///
/// <para>Global-vs-org como el resto del catálogo (org_id null = plantilla global del seed).</para>
/// </summary>
public sealed class ValidationRejectionReason
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "RERUN_REQUIRED", "NEW_SAMPLE_REQUIRED".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Nombre display. Ej: "Repetir la corrida".</summary>
    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>
    /// El motivo obliga operativamente a pedir una muestra nueva (no alcanza con repetir la
    /// corrida sobre la misma). Lo usa la UI para avisarle al técnico qué se espera de él.
    /// </summary>
    public bool RequiresNewSample { get; private set; }

    /// <summary>Orden de presentación en el selector.</summary>
    public int SortOrder { get; private set; }

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private ValidationRejectionReason() { }

    public static ValidationRejectionReason Create(
        string code,
        string name,
        string? description = null,
        bool requiresNewSample = false,
        int sortOrder = 0,
        Guid? organizationId = null,
        bool active = true,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("El nombre es obligatorio.", nameof(name));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new ValidationRejectionReason
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            Code = code.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            RequiresNewSample = requiresNewSample,
            SortOrder = sortOrder,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
