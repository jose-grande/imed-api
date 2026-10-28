namespace IMed.Api.Domain.Inventory;

/// <summary>
/// Una unidad base de la lista de la organización (tableta, frasco, ml…): la unidad en la que un
/// artículo cuenta sus existencias.
///
/// <para><b>Por qué una lista y no texto libre.</b> Con texto libre un artículo queda en "tab", otro
/// en "tableta" y otro en "tabs", y el reporte de existencias los muestra como unidades distintas.
/// La lista la administra el inventario; el artículo guarda el nombre (no el id) porque es lo que
/// imprimen el kardex y la etiqueta, y un renombre se propaga a los artículos que la usan.</para>
///
/// <para><b>No se borra, se desactiva.</b> Desactivarla la saca del selector de artículos nuevos;
/// los que ya la usan la conservan.</para>
/// </summary>
public sealed class InventoryUnit
{
    public const int NameMaxLength = 30;

    /// <summary>
    /// La lista con la que arranca cada organización. Cubre lo habitual de farmacia (formas de
    /// dosificación), laboratorio (pruebas, tubos) e insumos (pares, rollos).
    /// </summary>
    public static readonly IReadOnlyList<string> Defaults =
    [
        "tableta", "cápsula", "frasco", "ampolla", "vial", "sobre", "tubo", "jeringa",
        "unidad", "ml", "g", "prueba", "par", "rollo",
    ];

    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>En minúsculas, como lo guarda <see cref="StockItem.BaseUnit"/>. Único en la organización.</summary>
    public string Name { get; private set; } = string.Empty;

    public bool Active { get; private set; } = true;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private InventoryUnit() { }

    public static InventoryUnit Create(Guid organizationId, string name, TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new InventoryUnit
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            Name = Normalize(name),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    public void Update(string name, bool active, TimeProvider? timeProvider = null)
    {
        Name = Normalize(name);
        Active = active;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>La forma en que se compara y se guarda: sin espacios sobrantes y en minúsculas.</summary>
    public static string Normalize(string? name)
    {
        var n = string.Join(' ', (name ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        if (n.Length == 0)
        {
            throw new ArgumentException("El nombre de la unidad es obligatorio.", nameof(name));
        }
        if (n.Length > NameMaxLength)
        {
            throw new ArgumentException($"El nombre de la unidad lleva hasta {NameMaxLength} caracteres.", nameof(name));
        }
        return n;
    }
}
