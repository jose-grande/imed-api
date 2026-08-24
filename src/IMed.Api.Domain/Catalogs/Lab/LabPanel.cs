namespace IMed.Api.Domain.Catalogs.Lab;

/// <summary>
/// Panel de laboratorio = agrupación de pruebas (Épica LIS-1, L1-D4).
///
/// <para>La composición de pruebas NO vive en el panel sino en sus
/// <see cref="LabPanelVersion"/>: cambiar la composición crea una versión nueva,
/// sin alterar reportes históricos que referencian versiones previas.</para>
/// </summary>
public sealed class LabPanel
{
    public Guid Id { get; private set; }

    /// <summary>Org dueña. Null = plantilla global del seed (L1-D1).</summary>
    public Guid? OrganizationId { get; private set; }

    /// <summary>Código del panel. Ej: "CMP", "CBC", "LIPID".</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    /// <summary>Código LOINC del panel cuando existe (CBC 58410-2, CMP 24323-8…). Dual coding (LIS-RD-22).</summary>
    public string? LoincCode { get; private set; }

    public bool Active { get; private set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private LabPanel() { }

    public static LabPanel Create(
        string code,
        string name,
        string? description = null,
        Guid? organizationId = null,
        bool active = true,
        string? loincCode = null,
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
        return new LabPanel
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Code = code.Trim(),
            Name = name.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            LoincCode = string.IsNullOrWhiteSpace(loincCode) ? null : loincCode.Trim(),
            Active = active,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Edit(string name, string? description, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Name es obligatorio.", nameof(name));
        }
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    public void SetActive(bool active, TimeProvider? timeProvider = null)
    {
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
