using IMed.Api.Application.Forms.Studio;

namespace IMed.Api.Application.Abstractions;

/// <summary>
/// Catálogos de campos de los formularios del studio, uno por tipo de formulario. Los define la
/// plataforma; las organizaciones no los editan.
/// </summary>
public interface IFieldCatalogProvider
{
    /// <summary>Tipos de formulario que tienen catálogo activo.</summary>
    Task<IReadOnlyList<FormCatalogSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>El catálogo activo de mayor versión del tipo de formulario, o null si no hay.</summary>
    Task<FieldCatalog?> FindAsync(string formCode, CancellationToken cancellationToken = default);

    /// <summary>JSON completo del catálogo, tal cual lo consume el cliente.</summary>
    Task<string?> GetCatalogJsonAsync(string formCode, CancellationToken cancellationToken = default);

    /// <summary>Descarta la caché para que la siguiente lectura vea los catálogos cargados.</summary>
    void Invalidate();
}
