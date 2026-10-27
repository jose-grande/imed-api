using IMed.Api.Application.Abstractions;

namespace IMed.Api.Application.Organizations;

/// <summary>
/// Acceso al logo de la organización. Separado de <c>IOrganizationProfileRepository</c> a
/// propósito: el perfil se lee en casi todos los caminos y no conviene que arrastre los bytes de
/// una imagen en cada lectura.
/// </summary>
public interface IOrganizationBrandingRepository
{
    /// <summary>Logo con sus bytes, para incrustarlo en un documento.</summary>
    Task<OrganizationLogoContent?> GetLogoAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Datos del logo <b>sin</b> los bytes, para la pantalla de configuración.</summary>
    Task<OrganizationLogoInfo?> GetLogoInfoAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Guarda o reemplaza el logo. Crea la fila si la organización no tenía.</summary>
    Task SaveLogoAsync(
        Guid organizationId,
        byte[] bytes,
        string contentType,
        string? fileName,
        Guid? updatedBy,
        CancellationToken ct = default);

    /// <summary>Quita el logo. <c>false</c> si no había ninguno.</summary>
    Task<bool> RemoveLogoAsync(Guid organizationId, Guid? updatedBy, CancellationToken ct = default);
}

/// <summary>Logo con bytes.</summary>
public sealed record OrganizationLogoContent(
    byte[] Bytes,
    string ContentType,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Convierte a lo que esperan los renderizadores, con una clave de caché estable.
    /// </summary>
    /// <remarks>
    /// MigraDoc cachea las imágenes por nombre y la caché es por proceso. La clave incluye la
    /// organización —para que un laboratorio no reciba el logo de otro— y el instante de la
    /// última modificación, para que al reemplazar el logo los documentos nuevos no sigan
    /// saliendo con el viejo hasta reiniciar el servicio.
    /// </remarks>
    public PdfLogo ToPdfLogo(Guid organizationId) =>
        new(Bytes, ContentType, $"org-logo-{organizationId:N}-{UpdatedAt.UtcTicks}");
}

/// <summary>Logo sin bytes: lo que la pantalla de configuración necesita mostrar.</summary>
public sealed record OrganizationLogoInfo(
    string ContentType,
    string? FileName,
    int SizeBytes,
    DateTimeOffset UpdatedAt);
