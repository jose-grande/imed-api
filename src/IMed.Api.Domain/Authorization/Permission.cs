using System.Diagnostics.CodeAnalysis;

namespace IMed.Api.Domain.Authorization;

/// <summary>
/// Permiso del catálogo: una capacidad nombrada que un rol puede tener (ADR-007).
///
/// <para><b>Es producto, no dato del tenant.</b> El catálogo se siembra desde código versionado
/// y se consulta desde la base. Una política que solo viviera en la base sería mutable en runtime
/// por cualquiera con permiso de escritura, y nadie podría reconstruir quién cambió la regla que
/// deja a un auxiliar anular una factura.</para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "La regla reserva el sufijo Permission para las clases de Code Access Security, que .NET dejó de usar hace años. Acá Permission es el término del dominio y renombrarlo solo alejaría el código del lenguaje del negocio.")]
public sealed class Permission
{
    /// <summary>Código estable, en minúsculas y por puntos: <c>lab.result.validate</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    /// <summary>Módulo al que pertenece — agrupa la pantalla de administración.</summary>
    public string Module { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    private Permission() { }

    public static Permission Create(string code, string module, string description)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("El código del permiso es obligatorio.", nameof(code));
        }
        return new Permission
        {
            Code = code.Trim().ToLowerInvariant(),
            Module = (module ?? string.Empty).Trim(),
            Description = (description ?? string.Empty).Trim(),
        };
    }
}

/// <summary>
/// Fila de la matriz: un rol tiene un permiso, opcionalmente con un alcance (ADR-007/ADR-008).
///
/// <para><c>OrganizationId</c> nulo = plantilla global del producto. Una fila con organización
/// es una variante de ese tenant, que gana sobre la global. Es el mismo patrón copy-on-create
/// del catálogo de laboratorio (rev 53).</para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "La regla reserva el sufijo Permission para las clases de Code Access Security, que .NET dejó de usar hace años. Acá Permission es el término del dominio y renombrarlo solo alejaría el código del lenguaje del negocio.")]
public sealed class RolePermission
{
    public Guid Id { get; private set; }

    /// <summary>Nulo = plantilla global; con valor = variante de esa organización.</summary>
    public Guid? OrganizationId { get; private set; }

    public string RoleCode { get; private set; } = string.Empty;
    public string PermissionCode { get; private set; } = string.Empty;

    /// <summary>
    /// Hasta dónde llega el permiso: <c>own</c>, <c>assigned</c>, <c>location</c>, <c>organization</c>.
    /// Contesta "sobre qué" una vez que el permiso contestó "qué".
    /// </summary>
    public PermissionScope Scope { get; private set; }

    /// <summary>
    /// Negación explícita. Permite que un tenant le quite a un rol algo que la plantilla global
    /// le da, sin tener que reescribir la matriz entera.
    /// </summary>
    public bool Denied { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private RolePermission() { }

    public static RolePermission Create(
        string roleCode,
        string permissionCode,
        PermissionScope scope = PermissionScope.Organization,
        Guid? organizationId = null,
        bool denied = false,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(roleCode))
        {
            throw new ArgumentException("El rol es obligatorio.", nameof(roleCode));
        }
        if (string.IsNullOrWhiteSpace(permissionCode))
        {
            throw new ArgumentException("El permiso es obligatorio.", nameof(permissionCode));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new RolePermission
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            RoleCode = roleCode.Trim(),
            PermissionCode = permissionCode.Trim().ToLowerInvariant(),
            Scope = scope,
            Denied = denied,
            CreatedAt = now,
        };
    }
}

/// <summary>Alcance de un permiso — la dimensión "sobre qué" (ADR-007 §4, ADR-008).</summary>
public enum PermissionScope
{
    /// <summary>Solo lo propio: lo que el usuario firmó o generó.</summary>
    Own = 0,

    /// <summary>Lo asignado: pacientes con cita o encuentro abierto a su nombre.</summary>
    Assigned = 1,

    /// <summary>Todo lo de las sedes donde el usuario opera.</summary>
    Location = 2,

    /// <summary>Todo lo de la organización.</summary>
    Organization = 3,
}

/// <summary>
/// Versión de la política de una organización. Se incrementa en cada cambio de la matriz.
///
/// <para><b>Lo difícil no es guardar, es invalidar.</b> El TTL sirve para propagar altas; no
/// sirve para una revocación. Si se degrada a alguien a las 10:00, tiene que perder el permiso a
/// las 10:00 y no cuando venza el TTL. Cada instancia compara esta versión —una lectura barata—
/// y recarga cuando cambió.</para>
/// </summary>
public sealed class PolicyVersion
{
    /// <summary>
    /// Organización. <see cref="Guid.Empty"/> = plantilla global.
    ///
    /// <para>Se usa el guid cero y no <c>null</c> porque esto es clave primaria, y Postgres no
    /// considera iguales dos <c>NULL</c>: con nulos podrían coexistir varias filas "globales".</para>
    /// </summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Clave de la fila global.</summary>
    public static readonly Guid GlobalScope = Guid.Empty;

    public long Version { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public Guid? UpdatedBy { get; private set; }

    private PolicyVersion() { }

    public static PolicyVersion Create(Guid organizationId, TimeProvider? timeProvider = null) => new()
    {
        OrganizationId = organizationId,
        Version = 1,
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow(),
    };

    public void Bump(Guid? updatedBy, TimeProvider? timeProvider = null)
    {
        Version++;
        UpdatedBy = updatedBy;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
