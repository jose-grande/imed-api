using IMed.Api.Application.Abstractions;
using IMed.Api.Domain.Organizations;

namespace IMed.Api.Application.Organizations;

public sealed record SetOrganizationLogoCommand(
    Guid OrganizationId,
    Guid? UpdatedBy,
    byte[] Bytes,
    /// <summary>
    /// Tipo declarado por el cliente. Se usa <b>solo</b> para el mensaje de error; el que se
    /// guarda es el que sale de inspeccionar los bytes.
    /// </summary>
    string? DeclaredContentType,
    string? FileName);

public sealed record SetOrganizationLogoResult(
    bool Success,
    string? Error,
    OrganizationLogoInfo? Logo);

/// <summary>
/// Guarda el logo de la organización, que aparece en el encabezado del informe de laboratorio,
/// la orden de exámenes, la receta y la factura.
/// </summary>
/// <remarks>
/// <para>Observación de QA (ronda 1, staging): «necesitamos agregarle el formato con logo de la
/// clínica». Las etiquetas de muestra quedan afuera por decisión del usuario — las hay de
/// 33×13 mm, donde el logo competiría con el código de barras.</para>
///
/// <para><b>El tipo que se guarda sale de los bytes.</b> El <c>Content-Type</c> del multipart y
/// la extensión del archivo los elige quien sube, así que no sirven para decidir nada: un .exe
/// renombrado a .png llega con <c>image/png</c>. Acá se identifica la imagen por su cabecera y,
/// si no es una, se rechaza.</para>
/// </remarks>
public sealed class SetOrganizationLogoHandler(
    IOrganizationBrandingRepository repository,
    IImageInspector inspector,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Lado mínimo aceptable, en píxeles. Por debajo de esto el logo sale como una mancha en el
    /// papel, y es casi siempre un archivo equivocado (un favicon, un ícono).
    /// </summary>
    public const int MinSidePixels = 32;

    /// <summary>
    /// Lado máximo. Un logo de más de 4000 px no aporta nada a 3 cm de ancho impreso, y hace el
    /// PDF innecesariamente grande.
    /// </summary>
    public const int MaxSidePixels = 4000;

    public async Task<SetOrganizationLogoResult> HandleAsync(
        SetOrganizationLogoCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Bytes is null || command.Bytes.Length == 0)
        {
            return Fail("El archivo llegó vacío.");
        }

        if (command.Bytes.Length > OrganizationBranding.MaxLogoBytes)
        {
            return Fail(
                $"El logo pesa {command.Bytes.Length / 1024} KB y el máximo es " +
                $"{OrganizationBranding.MaxLogoBytes / 1024} KB.");
        }

        // Acá se decide el tipo: por el contenido, no por lo que declaró el cliente.
        var inspection = inspector.Inspect(command.Bytes);
        if (inspection is null)
        {
            var declared = string.IsNullOrWhiteSpace(command.DeclaredContentType)
                ? "sin tipo declarado"
                : $"declarado como '{command.DeclaredContentType}'";
            return Fail($"El archivo no es una imagen reconocible ({declared}).");
        }

        if (!OrganizationBranding.AllowedContentTypes.Contains(
                inspection.ContentType, StringComparer.OrdinalIgnoreCase))
        {
            return Fail(
                $"El archivo es {inspection.ContentType} y solo se admite PNG o JPEG. " +
                "Convertí el logo a uno de esos formatos y volvé a subirlo.");
        }

        if (inspection.Width < MinSidePixels || inspection.Height < MinSidePixels)
        {
            return Fail(
                $"La imagen mide {inspection.Width}×{inspection.Height} px. " +
                $"El mínimo es {MinSidePixels} px por lado — más chica que eso sale ilegible impresa.");
        }

        if (inspection.Width > MaxSidePixels || inspection.Height > MaxSidePixels)
        {
            return Fail(
                $"La imagen mide {inspection.Width}×{inspection.Height} px y el máximo es " +
                $"{MaxSidePixels} px por lado. Reducila antes de subirla.");
        }

        await repository.SaveLogoAsync(
            command.OrganizationId,
            command.Bytes,
            inspection.ContentType,
            command.FileName,
            command.UpdatedBy,
            ct);

        return new SetOrganizationLogoResult(
            Success: true,
            Error: null,
            Logo: new OrganizationLogoInfo(
                ContentType: inspection.ContentType,
                FileName: command.FileName,
                SizeBytes: command.Bytes.Length,
                UpdatedAt: timeProvider.GetUtcNow()));
    }

    private static SetOrganizationLogoResult Fail(string error) => new(false, error, null);
}

public sealed record GetOrganizationLogoQuery(Guid OrganizationId);

/// <summary>
/// Devuelve el logo para mostrarlo en la pantalla de configuración. El mismo handler sirve para
/// la vista previa (con bytes) y para el estado de la pantalla (sin ellos).
/// </summary>
public sealed class GetOrganizationLogoHandler(IOrganizationBrandingRepository repository)
{
    public Task<OrganizationLogoInfo?> GetInfoAsync(
        GetOrganizationLogoQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return repository.GetLogoInfoAsync(query.OrganizationId, ct);
    }

    public Task<OrganizationLogoContent?> GetContentAsync(
        GetOrganizationLogoQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return repository.GetLogoAsync(query.OrganizationId, ct);
    }
}

public sealed record RemoveOrganizationLogoCommand(Guid OrganizationId, Guid? UpdatedBy);

/// <summary>Quita el logo. Los documentos vuelven a emitirse con el encabezado de texto solo.</summary>
public sealed class RemoveOrganizationLogoHandler(IOrganizationBrandingRepository repository)
{
    public Task<bool> HandleAsync(
        RemoveOrganizationLogoCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return repository.RemoveLogoAsync(command.OrganizationId, command.UpdatedBy, ct);
    }
}
