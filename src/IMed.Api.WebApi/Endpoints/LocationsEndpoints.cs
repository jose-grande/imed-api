using System.Security.Claims;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;
using IMed.Api.Application.Locations;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Sedes de la organización (ADR-008). Bajo <c>/api/locations</c>.
///
/// <para>Leer las sedes lo puede hacer cualquiera con sesión: el selector de sede activa aparece
/// en toda pantalla operativa. Crearlas, renombrarlas y asignar usuarios es de administración.</para>
/// </summary>
public static class LocationsEndpoints
{
    public static IEndpointRouteBuilder MapLocationsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/locations")
            .WithTags("Locations")
            .RequireAuthorization();

        group.MapGet("/", ListAsync)
            .WithName("ListLocations")
            .WithSummary("Sedes de la organización.");

        group.MapGet("/mine", ListMineAsync)
            .WithName("ListMyLocations")
            .WithSummary("Sedes donde el usuario puede operar. Alimenta el selector de sede activa.");

        // Cualquiera con sesión: es lo que llena el selector de médico al dar una cita.
        group.MapGet("/{locationId:guid}/practitioners", ListPractitionersAsync)
            .WithName("ListLocationPractitioners")
            .WithSummary("Ids FHIR de los profesionales que atienden en la sede.");

        group.MapPost("/", CreateAsync)
            .WithName("CreateLocation")
            .WithSummary("Crea una sede.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        group.MapPatch("/{locationId:guid}", UpdateAsync)
            .WithName("UpdateLocation")
            .WithSummary("Renombra, activa/desactiva o designa una sede como principal.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        // Va con `billing.supervise` y no con el permiso de sedes: el código sale del mismo trámite
        // del SAR que el CAI y lo registra la misma persona. `org_admin` también lo tiene.
        group.MapPut("/{locationId:guid}/fiscal-establishment", SetEstablishmentAsync)
            .WithName("SetLocationFiscalEstablishment")
            .WithSummary("Fija o borra el código de establecimiento fiscal de una sede.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        group.MapPost("/{locationId:guid}/users/{userId:guid}", AssignAsync)
            .WithName("AssignUserToLocation")
            .WithSummary("Habilita a un usuario a operar en una sede.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        group.MapDelete("/{locationId:guid}/users/{userId:guid}", UnassignAsync)
            .WithName("UnassignUserFromLocation")
            .WithSummary("Quita a un usuario de una sede.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        // El conjunto entero de una vez (FAC-9): agregar y quitar por separado pasa por estados que
        // la invariante de "al menos una sede" no admite.
        group.MapPut("/users/{userId:guid}", SetUserLocationsAsync)
            .WithName("SetUserLocations")
            .WithSummary("Fija las sedes de un usuario y su sede principal.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        var legal = app.MapGroup("/api/legal-entities")
            .WithTags("Locations")
            .RequireAuthorization();

        // Leerlas no exige permiso propio: es la razón social y el RTN de la propia clínica, que
        // salen impresos en cada factura, y el formulario de sede los necesita para elegir.
        legal.MapGet("/", ListLegalEntitiesAsync)
            .WithName("ListLegalEntities")
            .WithSummary("Razones sociales de la organización (FAC-9).");

        legal.MapPut("/{legalEntityId:guid}", UpdateLegalEntityAsync)
            .WithName("UpdateLegalEntity")
            .WithSummary("Corrige los datos fiscales de una razón social. El RTN se bloquea tras la primera emisión.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgLocationsManage));

        return app;
    }

    private static async Task<IResult> ListLegalEntitiesAsync(
        ClaimsPrincipal principal, ListLegalEntitiesHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        return orgId is null ? Forbidden() : Results.Ok(await handler.HandleAsync(orgId.Value, ct));
    }

    private static async Task<IResult> UpdateLegalEntityAsync(
        Guid legalEntityId, UpdateLegalEntityRequest request, ClaimsPrincipal principal,
        UpdateLegalEntityHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var updated = await handler.HandleAsync(new UpdateLegalEntityCommand(
                orgId.Value, legalEntityId, request.LegalName, request.TaxId, request.FiscalAddress,
                request.TradeName, request.Phone, request.Email), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
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

    private static async Task<IResult> ListAsync(
        bool? includeInactive, ClaimsPrincipal principal, ListLocationsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, includeInactive ?? false, ct));
    }

    private static async Task<IResult> ListPractitionersAsync(
        Guid locationId, ClaimsPrincipal principal, ListLocationPractitionersHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var ids = await handler.HandleAsync(orgId.Value, locationId, ct);
        return ids is null ? Results.NotFound() : Results.Ok(ids);
    }

    private static async Task<IResult> ListMineAsync(
        ClaimsPrincipal principal, ListMyLocationsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        var userId = ParseSub(principal);
        if (orgId is null || userId is null)
        {
            return Forbidden();
        }
        return Results.Ok(await handler.HandleAsync(orgId.Value, userId.Value, ct));
    }

    private static async Task<IResult> CreateAsync(
        CreateLocationRequest request, ClaimsPrincipal principal, CreateLocationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var created = await handler.HandleAsync(
                new CreateLocationCommand(
                    orgId.Value, request.Code, request.Name, request.Address, request.Phone,
                    request.LegalEntityId, request.NewLegalEntity), ct);
            return Results.Ok(created);
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

    private static async Task<IResult> UpdateAsync(
        Guid locationId, UpdateLocationRequest request, ClaimsPrincipal principal,
        UpdateLocationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var updated = await handler.HandleAsync(
                new UpdateLocationCommand(
                    orgId.Value, locationId, request.Name, request.Active, request.MakeDefault,
                    request.Address, request.Phone, request.LegalEntityId, request.NewLegalEntity), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
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

    private static async Task<IResult> SetEstablishmentAsync(
        Guid locationId, SetEstablishmentRequest request, ClaimsPrincipal principal,
        SetEstablishmentCodeHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            var updated = await handler.HandleAsync(
                new SetEstablishmentCodeCommand(orgId.Value, locationId, request.Code), ct);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<IResult> AssignAsync(
        Guid locationId, Guid userId, AssignLocationRequest? request, ClaimsPrincipal principal,
        AssignUserToLocationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        var ok = await handler.HandleAsync(
            new AssignUserToLocationCommand(
                orgId.Value, locationId, userId, request?.MakePrimary ?? false, ParseSub(principal)),
            ct);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> UnassignAsync(
        Guid locationId, Guid userId, ClaimsPrincipal principal,
        UnassignUserFromLocationHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            await handler.HandleAsync(orgId.Value, locationId, userId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Estado inválido", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> SetUserLocationsAsync(
        Guid userId, SetUserLocationsRequest request, ClaimsPrincipal principal,
        SetUserLocationsHandler handler, CancellationToken ct)
    {
        var orgId = ParseOrganizationId(principal);
        if (orgId is null)
        {
            return Forbidden();
        }
        try
        {
            return Results.Ok(await handler.HandleAsync(new SetUserLocationsCommand(
                orgId.Value, userId, request.LocationIds ?? [], request.PrimaryLocationId, ParseSub(principal)), ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static IResult Forbidden() =>
        Results.Problem(title: "Sin organización", detail: "El token no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);

    private static Guid? ParseOrganizationId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return Guid.TryParse(raw, out var sub) ? sub : null;
    }
}

/// <summary>
/// Alta de sede. Sin <c>LegalEntityId</c> ni <c>NewLegalEntity</c>, la sede emite con la razón social
/// de la sede principal (FAC-9).
/// </summary>
public sealed record CreateLocationRequest(
    string Code,
    string Name,
    string? Address = null,
    string? Phone = null,
    Guid? LegalEntityId = null,
    NewLegalEntityInput? NewLegalEntity = null);

/// <summary>Campos null = no cambian. <c>Address</c>/<c>Phone</c> vacíos = se borran.</summary>
public sealed record UpdateLocationRequest(
    string? Name,
    bool? Active,
    bool? MakeDefault,
    string? Address = null,
    string? Phone = null,
    Guid? LegalEntityId = null,
    NewLegalEntityInput? NewLegalEntity = null);

/// <summary>Tres dígitos, o vacío/null para dejar la sede sin código.</summary>
public sealed record SetEstablishmentRequest(string? Code);

public sealed record AssignLocationRequest(bool MakePrimary);

public sealed record UpdateLegalEntityRequest(
    string LegalName,
    string? TaxId,
    string? FiscalAddress,
    string? TradeName,
    string? Phone,
    string? Email);

public sealed record SetUserLocationsRequest(IReadOnlyList<Guid>? LocationIds, Guid PrimaryLocationId);
