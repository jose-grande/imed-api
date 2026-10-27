using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Invitations;
using IMed.Api.Domain.Invitations;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints REST para gestión de invitaciones (Épica F3). Todas las rutas
/// requieren rol <c>org_admin</c> (verificado manualmente desde el claim
/// <c>realm_access.roles</c> que emite Keycloak — KC no expone los roles como
/// claim plano, así que <c>RequireRole</c> de ASP.NET no los ve sin mapear).
/// </summary>
public static class InvitationsEndpoints
{
    public static IEndpointRouteBuilder MapInvitationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/invitations")
            .WithTags("Invitations")
            .RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .WithName("CreateInvitation")
            .WithSummary("Emite una invitación por email a un usuario.");

        group.MapGet("/", ListPendingAsync)
            .WithName("ListPendingInvitations")
            .WithSummary("Lista las invitaciones pendientes de la organización del JWT.");

        group.MapDelete("/{id:guid}", RevokeAsync)
            .WithName("RevokeInvitation")
            .WithSummary("Revoca una invitación pendiente.");

        group.MapPost("/cleanup-expired", CleanupExpiredAsync)
            .WithName("CleanupExpiredInvitations")
            .WithSummary("Marca como expiradas las invitaciones más viejas que el threshold (default: 7 días).");

        return app;
    }

    private static async Task<IResult> CreateAsync(
        CreateInvitationRequest request,
        ClaimsPrincipal principal,
        CreateInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        InvitationRole role;
        try
        {
            role = InvitationRoleNames.FromString(request.Role);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            var result = await handler.HandleAsync(new CreateInvitationCommand(
                OrganizationId: orgId!.Value,
                CreatedBy: userId!.Value,
                Email: request.Email,
                Role: role,
                SpecialtyHint: request.SpecialtyHint,
                LocationIds: request.LocationIds), cancellationToken);

            return Results.Created($"/api/invitations/{result.InvitationId}", new CreateInvitationResponse(
                InvitationId: result.InvitationId,
                KeycloakUserId: result.KeycloakUserId,
                EmailDispatched: result.EmailDispatched));
        }
        catch (InvitationConflictException ex)
        {
            return Results.Problem(
                title: "Conflicto de invitación",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> ListPendingAsync(
        ClaimsPrincipal principal,
        ListPendingInvitationsHandler handler,
        CancellationToken cancellationToken)
    {
        var (_, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        var items = await handler.HandleAsync(new ListPendingInvitationsQuery(orgId!.Value), cancellationToken);
        return Results.Ok(items);
    }

    private static async Task<IResult> RevokeAsync(
        Guid id,
        ClaimsPrincipal principal,
        RevokeInvitationHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        try
        {
            await handler.HandleAsync(new RevokeInvitationCommand(
                InvitationId: id,
                OrganizationId: orgId!.Value,
                RevokedBy: userId!.Value), cancellationToken);
            return Results.NoContent();
        }
        catch (InvitationConflictException ex) when (ex.Code == "not_found")
        {
            return Results.Problem(title: "No encontrada", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (InvitationConflictException ex)
        {
            return Results.Problem(
                title: "No se puede revocar",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }

    private static async Task<IResult> CleanupExpiredAsync(
        CleanupExpiredRequest? request,
        ClaimsPrincipal principal,
        MarkInvitationsExpiredHandler handler,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        // Default threshold: 7 días — mismo lifespan default del action token KC.
        var lifespanSeconds = request?.LifespanSeconds ?? 7 * 24 * 3600;
        var threshold = timeProvider.GetUtcNow().AddSeconds(-lifespanSeconds);

        var marked = await handler.HandleAsync(new MarkInvitationsExpiredCommand(
            OrganizationId: orgId!.Value,
            Threshold: threshold), userId!.Value, cancellationToken);

        return Results.Ok(new CleanupExpiredResponse(MarkedCount: marked, Threshold: threshold));
    }

    private static (Guid? userId, Guid? orgId, IResult? error) ExtractIds(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(sub, out var userId))
        {
            return (null, null, Results.Problem(
                title: "Token inválido",
                detail: "El JWT no contiene un claim `sub` parseable como UUID.",
                statusCode: StatusCodes.Status401Unauthorized));
        }

        var orgRaw = principal.FindFirstValue("organization_id");
        if (!Guid.TryParse(orgRaw, out var orgId))
        {
            return (null, null, Results.Problem(
                title: "Sin organización",
                detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        if (!HasRealmRole(principal, InvitationRoleNames.OrgAdmin))
        {
            return (null, null, Results.Problem(
                title: "Sin permisos",
                detail: $"Esta operación requiere el rol '{InvitationRoleNames.OrgAdmin}'.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (userId, orgId, null);
    }

    /// <summary>
    /// Lee los realm roles desde el claim <c>realm_access</c> (Keycloak los
    /// emite como objeto JSON anidado). El config de
    /// <c>JwtBearerOptions.RoleClaimType</c> no aplica a claims anidados — por
    /// eso lo parseamos a mano.
    /// </summary>
    private static bool HasRealmRole(ClaimsPrincipal principal, string roleName)
    {
        var realmAccess = principal.FindFirstValue("realm_access");
        if (string.IsNullOrWhiteSpace(realmAccess))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(realmAccess);
            if (doc.RootElement.TryGetProperty("roles", out var rolesEl)
                && rolesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rolesEl.EnumerateArray())
                {
                    if (string.Equals(r.GetString(), roleName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }
        return false;
    }
}

/// <param name="LocationIds">Sedes del invitado; la primera es su principal (FAC-9).</param>
public sealed record CreateInvitationRequest(
    string Email, string Role, string? SpecialtyHint, IReadOnlyList<Guid>? LocationIds = null);

public sealed record CreateInvitationResponse(Guid InvitationId, Guid KeycloakUserId, bool EmailDispatched);

public sealed record CleanupExpiredRequest(int? LifespanSeconds);

public sealed record CleanupExpiredResponse(int MarkedCount, DateTimeOffset Threshold);
