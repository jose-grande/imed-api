namespace IMed.Api.Domain.Organizations;

/// <summary>
/// Identidad visual de una organización: por ahora, su logo.
/// </summary>
/// <remarks>
/// <para>Tabla propia y no una columna en <c>organization_subscriptions</c> por dos razones. Una
/// es semántica: el logo no tiene nada que ver con el contrato. La otra es práctica — son bytes de
/// una imagen, y no conviene que viajen en cada lectura del perfil de la organización, que se hace
/// en casi todos los caminos.</para>
///
/// <para><b>Los bytes van en la base y no en disco.</b> El respaldo de esta instalación cubre
/// PostgreSQL (guía de despliegue 08 §1); lo que vive en <c>/opt</c> o <c>/etc</c> queda fuera, y
/// ese agujero ya está registrado como deuda para el código de HAPI y los archivos de entorno. Un
/// logo son decenas de kilobytes: ponerlo en la base lo hace viajar con el <c>pg_dump</c> diario
/// en vez de perderse con la VM.</para>
/// </remarks>
public sealed class OrganizationBranding
{
    /// <summary>Tamaño máximo aceptado, en bytes. Un logo razonable no se acerca a esto.</summary>
    public const int MaxLogoBytes = 1024 * 1024;

    /// <summary>Tipos de imagen aceptados.</summary>
    /// <remarks>
    /// Solo PNG y JPEG: son los que el renderizador de PDF sabe incrustar y los que cualquiera
    /// puede producir. SVG queda afuera a propósito — es un documento ejecutable, y aceptar
    /// marcado de un tenant para incrustarlo en un PDF abre una superficie que no hace falta.
    /// </remarks>
    public static readonly string[] AllowedContentTypes = ["image/png", "image/jpeg"];

    /// <summary>Organización dueña. Es también la clave primaria: una fila por organización.</summary>
    public Guid OrganizationId { get; private set; }

    /// <summary>Bytes de la imagen. <c>null</c> = la organización no cargó logo.</summary>
    public byte[]? LogoBytes { get; private set; }

    /// <summary>Tipo de contenido de <see cref="LogoBytes"/>.</summary>
    public string? LogoContentType { get; private set; }

    /// <summary>Nombre del archivo original, solo para mostrarlo en la pantalla de carga.</summary>
    public string? LogoFileName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Usuario de Keycloak que cargó el logo vigente.</summary>
    public Guid? UpdatedBy { get; private set; }

    /// <summary>¿Hay un logo cargado y utilizable?</summary>
    public bool HasLogo => LogoBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(LogoContentType);

    private OrganizationBranding() { }

    public static OrganizationBranding Create(Guid organizationId, TimeProvider? timeProvider = null)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("OrganizationId es obligatorio.", nameof(organizationId));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new OrganizationBranding
        {
            OrganizationId = organizationId,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Reemplaza el logo.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Los bytes están vacíos, superan <see cref="MaxLogoBytes"/>, o el tipo de contenido no está
    /// en <see cref="AllowedContentTypes"/>.
    /// </exception>
    public void SetLogo(
        byte[] bytes,
        string contentType,
        string? fileName,
        Guid? updatedBy,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
        {
            throw new ArgumentException("El archivo está vacío.", nameof(bytes));
        }
        if (bytes.Length > MaxLogoBytes)
        {
            throw new ArgumentException(
                $"El logo supera el máximo de {MaxLogoBytes / 1024} KB.", nameof(bytes));
        }
        if (!AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Tipo no aceptado: '{contentType}'. Se admite {string.Join(" o ", AllowedContentTypes)}.",
                nameof(contentType));
        }

        LogoBytes = bytes;
        LogoContentType = contentType.ToLowerInvariant();
        LogoFileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        UpdatedBy = updatedBy;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>Quita el logo. Los documentos vuelven a emitirse sin él.</summary>
    public void RemoveLogo(Guid? updatedBy, TimeProvider? timeProvider = null)
    {
        LogoBytes = null;
        LogoContentType = null;
        LogoFileName = null;
        UpdatedBy = updatedBy;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }
}
