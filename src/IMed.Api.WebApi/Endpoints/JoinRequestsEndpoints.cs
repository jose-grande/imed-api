using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.JoinRequests;
using IMed.Api.Domain.Invitations;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Endpoints REST del flow de join requests (Épica F4):
/// <list type="bullet">
///   <item><c>POST /api/join-requests</c> — público (sin auth). El form lo
///   sirve el frontend en <c>/join</c>.</item>
///   <item><c>GET /api/join-requests</c>, <c>POST /{id}/approve</c>,
///   <c>POST /{id}/reject</c> — requieren rol <c>org_admin</c> (verificación
///   manual desde claim <c>realm_access.roles</c>).</item>
/// </list>
/// </summary>
public static class JoinRequestsEndpoints
{
    public static IEndpointRouteBuilder MapJoinRequestsEndpoints(this IEndpointRouteBuilder app)
    {
        // Endpoint público — sin RequireAuthorization. Es el formulario que un
        // candidato anónimo envía para pedir ingreso.
        app.MapPost("/api/join-requests", SubmitAsync)
            .WithTags("Join requests")
            .WithName("SubmitJoinRequest")
            .WithSummary("Solicitud pública de ingreso a una organización (sin auth).")
            .AllowAnonymous();

        var adminGroup = app.MapGroup("/api/join-requests")
            .WithTags("Join requests (admin)")
            .RequireAuthorization();

        adminGroup.MapGet("/", ListPendingAsync)
            .WithName("ListPendingJoinRequests")
            .WithSummary("Lista las solicitudes pendientes de la organización del JWT.");

        adminGroup.MapPost("/{id:guid}/approve", ApproveAsync)
            .WithName("ApproveJoinRequest")
            .WithSummary("Aprueba una solicitud y emite invitation con role default 'practitioner'.");

        adminGroup.MapPost("/{id:guid}/reject", RejectAsync)
            .WithName("RejectJoinRequest")
            .WithSummary("Rechaza una solicitud con razón opcional.");

        return app;
    }

    private static async Task<IResult> SubmitAsync(
        SubmitJoinRequestRequest request,
        SubmitJoinRequestHandler handler,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await handler.HandleAsync(new SubmitJoinRequestCommand(
                Email: request.Email,
                FullName: request.FullName,
                OrganizationFhirId: request.OrganizationFhirId,
                Message: request.Message), cancellationToken);

            return Results.Created($"/api/join-requests/{result.JoinRequestId}", new SubmitJoinRequestResponse(
                JoinRequestId: result.JoinRequestId,
                AdminNotified: result.AdminNotified));
        }
        catch (JoinRequestConflictException ex) when (ex.Code == "organization_not_found")
        {
            return Results.Problem(
                title: "Organización no encontrada",
                detail: ex.Message,
                statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
        catch (JoinRequestConflictException ex)
        {
            return Results.Problem(
                title: "Conflicto de solicitud",
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
        ListPendingJoinRequestsHandler handler,
        CancellationToken cancellationToken)
    {
        var (_, orgId, error) = ExtractAdminIds(principal);
        if (error is not null)
        {
            return error;
        }

        var items = await handler.HandleAsync(new ListPendingJoinRequestsQuery(orgId!.Value), cancellationToken);
        return Results.Ok(items);
    }

    private static async Task<IResult> ApproveAsync(
        Guid id,
        ApproveJoinRequestRequest? request,
        ClaimsPrincipal principal,
        ApproveJoinRequestHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractAdminIds(principal);
        if (error is not null)
        {
            return error;
        }

        var role = InvitationRole.Practitioner;
        if (!string.IsNullOrWhiteSpace(request?.Role))
        {
            if (!InvitationRoleNames.TryParseRealmRole(request.Role, out role))
            {
                return Results.Problem(
                    title: "Rol inválido",
                    detail: $"'{request.Role}' no es un rol soportado. Valores válidos: org_admin, practitioner, auxiliar.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        try
        {
            var result = await handler.HandleAsync(new ApproveJoinRequestCommand(
                JoinRequestId: id,
                OrganizationId: orgId!.Value,
                ResolvedBy: userId!.Value,
                Role: role,
                LocationIds: request?.LocationIds), cancellationToken);
            return Results.Ok(new ApproveJoinRequestResponse(
                JoinRequestId: result.JoinRequestId,
                LinkedInvitationId: result.LinkedInvitationId,
                ApplicantNotified: result.ApplicantNotified));
        }
        catch (JoinRequestConflictException ex) when (ex.Code == "not_found")
        {
            return Results.Problem(title: "No encontrada", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (JoinRequestConflictException ex)
        {
            return Results.Problem(
                title: "No se puede aprobar",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }

    private static async Task<IResult> RejectAsync(
        Guid id,
        RejectJoinRequestRequest? request,
        ClaimsPrincipal principal,
        RejectJoinRequestHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractAdminIds(principal);
        if (error is not null)
        {
            return error;
        }

        try
        {
            await handler.HandleAsync(new RejectJoinRequestCommand(
                JoinRequestId: id,
                OrganizationId: orgId!.Value,
                ResolvedBy: userId!.Value,
                Reason: request?.Reason), cancellationToken);
            return Results.NoContent();
        }
        catch (JoinRequestConflictException ex) when (ex.Code == "not_found")
        {
            return Results.Problem(title: "No encontrada", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (JoinRequestConflictException ex)
        {
            return Results.Problem(
                title: "No se puede rechazar",
                detail: ex.Message,
                statusCode: StatusCodes.Status409Conflict,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code });
        }
    }

    private static (Guid? userId, Guid? orgId, IResult? error) ExtractAdminIds(ClaimsPrincipal principal)
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

public sealed record SubmitJoinRequestRequest(
    string Email,
    string FullName,
    string OrganizationFhirId,
    string? Message);

public sealed record SubmitJoinRequestResponse(Guid JoinRequestId, bool AdminNotified);

/// <param name="LocationIds">Sedes del solicitante; la primera es su principal (FAC-9).</param>
public sealed record ApproveJoinRequestRequest(string? Role, IReadOnlyList<Guid>? LocationIds = null);

public sealed record ApproveJoinRequestResponse(Guid JoinRequestId, Guid LinkedInvitationId, bool ApplicantNotified);

public sealed record RejectJoinRequestRequest(string? Reason);
