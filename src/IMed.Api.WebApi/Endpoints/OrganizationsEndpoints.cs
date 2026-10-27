using System.Security.Claims;
using IMed.Api.Application.Organizations;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Organizations;
using IMed.Api.WebApi.Http;
using Microsoft.AspNetCore.Mvc;

namespace IMed.Api.WebApi.Endpoints;

public static class OrganizationsEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/organizations")
            .WithTags("Organizations")
            .RequireAuthorization();

        group.MapPost("/bootstrap", BootstrapAsync)
            .WithName("BootstrapOrganization")
            .WithSummary("Crea la primera Organization del usuario recién registrado.");

        // ── Logo (observación de QA ronda 1: «formato con logo de la clínica») ──
        //
        // Son los primeros endpoints multipart de la API. El tope de tamaño se declara en el
        // endpoint y no solo se verifica en el handler: sin él, ASP.NET bufferea el cuerpo
        // entero —a disco, pasados 64 KB— antes de que nadie pueda mirarlo, así que un POST de
        // 500 MB se escribiría completo para después rechazarse.
        //
        // `DisableAntiforgery` porque esta API se autentica con bearer token y no con cookies:
        // sin cookie de sesión no hay CSRF que prevenir, y el middleware exigiría un token que
        // un cliente de API no tiene de dónde sacar.
        group.MapGet("/me/logo", GetLogoAsync)
            .WithName("GetMyOrganizationLogo")
            .WithSummary("Devuelve el logo de la organización como imagen, para la vista previa.");

        group.MapGet("/me/logo/info", GetLogoInfoAsync)
            .WithName("GetMyOrganizationLogoInfo")
            .WithSummary("Datos del logo sin los bytes: tipo, nombre, tamaño y fecha.");

        group.MapPut("/me/logo", SetLogoAsync)
            .WithName("SetMyOrganizationLogo")
            .WithSummary("Carga o reemplaza el logo que aparece en los documentos impresos.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgManage))
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes));

        group.MapDelete("/me/logo", RemoveLogoAsync)
            .WithName("RemoveMyOrganizationLogo")
            .WithSummary("Quita el logo. Los documentos vuelven a emitirse sin él.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgManage));

        return app;
    }

    /// <summary>
    /// Tope del cuerpo de la petición: el máximo del logo más un margen para los encabezados
    /// del multipart. Lo que pase de acá lo corta Kestrel; lo que entre pero exceda el máximo
    /// del logo lo rechaza el handler con un mensaje que el usuario entiende.
    /// </summary>
    private const long MaxUploadBytes = OrganizationBranding.MaxLogoBytes + (64 * 1024);

    private static async Task<IResult> BootstrapAsync(
        BootstrapOrganizationRequest request,
        ClaimsPrincipal user,
        BootstrapOrganizationHandler handler,
        CancellationToken cancellationToken)
    {
        // `sub` del JWT = id del usuario en Keycloak. Es un UUID.
        var sub = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(sub, out var keycloakUserId))
        {
            return Results.Problem(
                title: "Token inválido",
                detail: "El JWT no contiene un claim `sub` parseable como UUID.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // Regla de negocio: solo usuarios SIN organization_id pueden hacer bootstrap.
        // Si ya tienen uno asignado, están intentando cruzar tenants — rechazar.
        var currentOrgId = user.FindFirstValue("organization_id");
        if (!string.IsNullOrWhiteSpace(currentOrgId))
        {
            return Results.Problem(
                title: "Usuario ya pertenece a una Organization",
                detail: $"El usuario ya tiene organization_id='{currentOrgId}'. El bootstrap solo aplica a usuarios recién registrados.",
                statusCode: StatusCodes.Status409Conflict);
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.Problem(
                title: "Nombre obligatorio",
                detail: "El campo `name` es obligatorio.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var result = await handler.HandleAsync(
            new BootstrapOrganizationCommand(
                KeycloakUserId: keycloakUserId,
                OrganizationName: request.Name,
                NationalTaxId: request.NationalTaxId),
            cancellationToken);

        var message = result.PendingKeycloakSync
            ? "Organization creada. La sincronización con Keycloak quedó pendiente; el sistema reintentará en background. Cuando recibas la notificación de refresh, cerrá sesión y volvé a loguearte."
            : "Organization creada. Cerrá sesión y volvé a loguearte para que el nuevo JWT traiga el organization_id.";

        return Results.Ok(new BootstrapOrganizationResponse(
            OrganizationId: result.OrganizationId,
            OrganizationFhirId: result.OrganizationFhirId,
            AssignedTier: result.AssignedTier.ToString().ToUpperInvariant(),
            PendingKeycloakSync: result.PendingKeycloakSync,
            Message: message));
    }

    // ── Logo ────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetLogoAsync(
        ClaimsPrincipal user,
        GetOrganizationLogoHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(user);
        if (orgId is null)
        {
            return ForbiddenOrg();
        }

        var logo = await handler.GetContentAsync(new GetOrganizationLogoQuery(orgId.Value), cancellationToken);
        if (logo is null)
        {
            return Results.NotFound();
        }

        // `ETag` a partir del instante de la última modificación: la vista previa de la
        // pantalla no tiene por qué volver a bajar la imagen en cada render.
        return Results.File(
            logo.Bytes,
            contentType: logo.ContentType,
            lastModified: logo.UpdatedAt,
            entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(
                $"\"{logo.UpdatedAt.UtcTicks}\""));
    }

    private static async Task<IResult> GetLogoInfoAsync(
        ClaimsPrincipal user,
        GetOrganizationLogoHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(user);
        if (orgId is null)
        {
            return ForbiddenOrg();
        }

        var info = await handler.GetInfoAsync(new GetOrganizationLogoQuery(orgId.Value), cancellationToken);

        // 200 con `hasLogo: false` y no 404: la pantalla pregunta «¿hay logo?», y esa pregunta
        // tiene respuesta aunque no haya ninguno.
        return Results.Ok(new OrganizationLogoResponse(
            HasLogo: info is not null,
            ContentType: info?.ContentType,
            FileName: info?.FileName,
            SizeBytes: info?.SizeBytes,
            UpdatedAt: info?.UpdatedAt,
            MaxSizeBytes: OrganizationBranding.MaxLogoBytes,
            AllowedContentTypes: OrganizationBranding.AllowedContentTypes));
    }

    private static async Task<IResult> SetLogoAsync(
        IFormFile? file,
        ClaimsPrincipal user,
        SetOrganizationLogoHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(user);
        if (orgId is null)
        {
            return ForbiddenOrg();
        }

        if (file is null || file.Length == 0)
        {
            return Results.Problem(
                title: "Archivo obligatorio",
                detail: "Enviá el logo en el campo `file` de un formulario multipart.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Se mira `Length` antes de leer: evita copiar a memoria un archivo que se va a
        // rechazar de todos modos.
        if (file.Length > OrganizationBranding.MaxLogoBytes)
        {
            return Results.Problem(
                title: "Logo demasiado grande",
                detail: $"El archivo pesa {file.Length / 1024} KB y el máximo es " +
                        $"{OrganizationBranding.MaxLogoBytes / 1024} KB.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        using var buffer = new MemoryStream(capacity: (int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);

        var result = await handler.HandleAsync(
            new SetOrganizationLogoCommand(
                OrganizationId: orgId.Value,
                UpdatedBy: ParseUserId(user),
                Bytes: buffer.ToArray(),
                DeclaredContentType: file.ContentType,
                FileName: file.FileName),
            cancellationToken);

        if (!result.Success)
        {
            return Results.Problem(
                title: "Logo rechazado",
                detail: result.Error,
                statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.Ok(new OrganizationLogoResponse(
            HasLogo: true,
            ContentType: result.Logo!.ContentType,
            FileName: result.Logo.FileName,
            SizeBytes: result.Logo.SizeBytes,
            UpdatedAt: result.Logo.UpdatedAt,
            MaxSizeBytes: OrganizationBranding.MaxLogoBytes,
            AllowedContentTypes: OrganizationBranding.AllowedContentTypes));
    }

    private static async Task<IResult> RemoveLogoAsync(
        ClaimsPrincipal user,
        RemoveOrganizationLogoHandler handler,
        CancellationToken cancellationToken)
    {
        var orgId = ParseOrganizationId(user);
        if (orgId is null)
        {
            return ForbiddenOrg();
        }

        var removed = await handler.HandleAsync(
            new RemoveOrganizationLogoCommand(orgId.Value, ParseUserId(user)), cancellationToken);

        return removed ? Results.NoContent() : Results.NotFound();
    }

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseUserId(ClaimsPrincipal principal) =>
        Guid.TryParse(
            principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"),
            out var id)
            ? id
            : null;

    private static IResult ForbiddenOrg() =>
        Results.Problem(
            title: "Sin organización",
            detail: "El JWT no trae organization_id.",
            statusCode: StatusCodes.Status403Forbidden);
}

/// <summary>
/// Estado del logo de la organización. Lleva los límites —tamaño máximo y formatos— para que la
/// pantalla no los repita por su cuenta: duplicados, se desincronizan.
/// </summary>
public sealed record OrganizationLogoResponse(
    bool HasLogo,
    string? ContentType,
    string? FileName,
    int? SizeBytes,
    DateTimeOffset? UpdatedAt,
    int MaxSizeBytes,
    IReadOnlyList<string> AllowedContentTypes);

public sealed record BootstrapOrganizationRequest(string Name, string? NationalTaxId);

public sealed record BootstrapOrganizationResponse(
    Guid OrganizationId,
    string OrganizationFhirId,
    string AssignedTier,
    bool PendingKeycloakSync,
    string Message);
