namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Motivo de rechazo de muestra preanalítico (R6-F1a, decisión UX §8.7). Catálogo
/// codificado que alimentará el QC de recepción (R6-F6) y normaliza los motivos que
/// hoy viven sueltos como el enum <see cref="IMed.Api.Domain.Lab.SpecimenIncident"/>.
/// Global-vs-org como el resto del catálogo (org_id null = plantilla global del seed).
/// </summary>
public sealed class SpecimenRejectionReason
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código interno. Ej: "HEMOLYSIS", "WRONG_CONTAINER".</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Nombre display. Ej: "Hemólisis".</summary>
    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>Si el rechazo obliga operativamente a solicitar una nueva muestra.</summary>
    public bool RequiresNewSample { get; private set; } = true;

    /// <summary>Orden de presentación en el checklist de QC.</summary>
    public int SortOrder { get; private set; }

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private SpecimenRejectionReason() { }

    public static SpecimenRejectionReason Create(
        string code,
        string name,
        string? description = null,
        bool requiresNewSample = true,
        int sortOrder = 0,
        Guid? organizationId = null,
        bool active = true,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("Code es obligatorio.", nameof(code));
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new SpecimenRejectionReason
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            RequiresNewSample = requiresNewSample,
            SortOrder = sortOrder,
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(string name, string? description, bool requiresNewSample, int sortOrder, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        RequiresNewSample = requiresNewSample;
        SortOrder = sortOrder;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
