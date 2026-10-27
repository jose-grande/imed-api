namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Funciones que se le suman a un miembro del equipo además de su rol principal (plan de salida a
/// producción, A1): cajero, facturador, roles del laboratorio, farmacia e inventario.
///
/// <para><b>Por qué aparte del rol principal.</b> El rol principal —administrador, médico,
/// auxiliar— dice quién es la persona en la organización y es uno solo. Las funciones dicen qué
/// estaciones opera, y en una clínica chica una misma persona cobra, recibe muestras y despacha
/// farmacia. Son realm roles de Keycloak como el principal; esta lista es la que la pantalla de
/// equipo puede dar y quitar, y deja fuera a propósito los roles principales y el de soporte.</para>
/// </summary>
public static class FunctionalRoles
{
    public static IReadOnlyList<string> All { get; } =
    [
        "billing_supervisor",
        "billing_clerk",
        "cashier",
        "lab_supervisor",
        "bioanalyst",
        "pathologist",
        "lab_tech",
        "pharmacist",
        "pharmacy_tech",
        "inventory_manager",
        "warehouse_keeper",
    ];

    public static bool IsFunctional(string role) => All.Contains(role, StringComparer.Ordinal);

    /// <summary>
    /// Normaliza lo pedido: sin duplicados, en el orden del catálogo. Lanza si viene algo que no es
    /// una función —un rol principal o uno inventado— porque concederlo por esta vía saltaría las
    /// reglas del cambio de rol (por ejemplo, el último administrador).
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? requested)
    {
        var pedidas = (requested ?? []).Select(r => r?.Trim() ?? string.Empty).ToList();
        var invalidas = pedidas.Where(r => !IsFunctional(r)).Distinct().ToList();
        if (invalidas.Count > 0)
        {
            throw new ArgumentException(
                $"No son funciones asignables: {string.Join(", ", invalidas)}.", nameof(requested));
        }
        return All.Where(pedidas.Contains).ToList();
    }
}
