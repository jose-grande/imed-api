using System.Security.Claims;
using IMed.Api.Application.Practitioners;

namespace IMed.Api.WebApi.Endpoints;

public static class MePractitionerProfileEndpoints
{
    public static IEndpointRouteBuilder MapMePractitionerProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me/practitioner-profile")
            .WithTags("Me — Practitioner profile")
            .RequireAuthorization();

        group.MapGet("/", GetAsync)
            .WithName("GetMyPractitionerProfile")
            .WithSummary("Devuelve el perfil profesional del user logueado en su org actual.");

        group.MapPut("/", UpsertAsync)
            .WithName("UpsertMyPractitionerProfile")
            .WithSummary("Crea o actualiza el Practitioner FHIR del user en su org actual.");

        return app;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        GetMyPractitionerProfileHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        // Email + nombres del JWT alimentan el lazy provision (Épica F3) — el
        // handler solo los usa si detecta una invitation pending para este user.
        var email = principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email");
        var given = principal.FindFirstValue(ClaimTypes.GivenName)
            ?? principal.FindFirstValue("given_name");
        var family = principal.FindFirstValue(ClaimTypes.Surname)
            ?? principal.FindFirstValue("family_name");

        var result = await handler.HandleAsync(
            new GetMyPractitionerProfileQuery(
                KeycloakUserId: userId!.Value,
                OrganizationId: orgId!.Value,
                Email: email,
                GivenName: given,
                FamilyName: family),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> UpsertAsync(
        UpsertMyPractitionerProfileRequest request,
        ClaimsPrincipal principal,
        UpsertMyPractitionerProfileHandler handler,
        CancellationToken cancellationToken)
    {
        var (userId, orgId, error) = ExtractIds(principal);
        if (error is not null)
        {
            return error;
        }

        try
        {
            var result = await handler.HandleAsync(
                new UpsertMyPractitionerProfileCommand(
                    KeycloakUserId: userId!.Value,
                    OrganizationId: orgId!.Value,
                    Family: request.Family,
                    Given: request.Given,
                    Prefix: request.Prefix,
                    LicenseNumber: request.LicenseNumber,
                    Email: request.Email,
                    Phone: request.Phone,
                    Gender: request.Gender,
                    SpecialtyCode: request.SpecialtyCode,
                    SpecialtyDisplay: request.SpecialtyDisplay),
                cancellationToken);
            return Results.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
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
                detail: "El JWT no trae organization_id. Bootstrap pendiente.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (userId, orgId, null);
    }
}

public sealed record UpsertMyPractitionerProfileRequest(
    string Family,
    IReadOnlyList<string> Given,
    string? Prefix,
    string? LicenseNumber,
    string? Email,
    string? Phone,
    string? Gender,
    string? SpecialtyCode,
    string? SpecialtyDisplay);
