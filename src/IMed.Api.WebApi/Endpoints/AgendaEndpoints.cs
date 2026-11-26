using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Agenda;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// La agenda para quien la mantiene, sin abrirle el expediente (<c>agenda.read</c>). Ver
/// <see cref="ListAgendaHandler"/>.
///
/// <para>Mover y reprogramar citas pide <c>record.write</c>, igual que escribirlas por el proxy:
/// lo que cambia es que acá se toca solo el estado o el horario, y lo demás de la cita se conserva
/// del lado del servidor.</para>
/// </summary>
public static class AgendaEndpoints
{
    public static IEndpointRouteBuilder MapAgendaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/agenda").WithTags("Agenda");

        group.MapGet("/appointments", ListAsync)
            .WithName("ListAgenda")
            .WithSummary("Citas del rango con el paciente identificado (nombre, identificación, teléfono) y sin datos clínicos.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.AgendaRead));

        group.MapPost("/appointments/{appointmentId}/status", ChangeStatusAsync)
            .WithName("ChangeAppointmentStatus")
            .WithSummary("Llegó, cancelada o no se presentó: cambia solo el estado de la cita.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordWrite));

        group.MapPost("/appointments/{appointmentId}/reschedule", RescheduleAsync)
            .WithName("RescheduleAppointment")
            .WithSummary("Cambia fecha, hora, duración, médico o tipo; conserva el resto de la cita.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordWrite));

        return app;
    }

    private static async Task<IResult> ListAsync(
        DateTimeOffset from, DateTimeOffset to, Guid? locationId, string? practitionerId, string? status,
        ClaimsPrincipal principal, IPermissionResolver resolver, ListAgendaHandler handler, CancellationToken ct)
    {
        var caller = await CallerAsync(principal, resolver, ct);
        if (caller is null)
        {
            return NoOrg();
        }
        var estados = (status ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return await Run(() => handler.HandleAsync(
            caller, new AgendaQuery(from, to, locationId, practitionerId, estados), ct), Results.Ok);
    }

    private static async Task<IResult> ChangeStatusAsync(
        string appointmentId, ChangeAppointmentStatusRequest request,
        ClaimsPrincipal principal, IPermissionResolver resolver, ChangeAppointmentStatusHandler handler, CancellationToken ct)
    {
        var caller = await CallerAsync(principal, resolver, ct);
        if (caller is null)
        {
            return NoOrg();
        }
        return await Run(() => handler.HandleAsync(caller, appointmentId, request.Status ?? string.Empty, ct),
            ok => ok ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<IResult> RescheduleAsync(
        string appointmentId, RescheduleAppointmentRequest request,
        ClaimsPrincipal principal, IPermissionResolver resolver, RescheduleAppointmentHandler handler, CancellationToken ct)
    {
        var caller = await CallerAsync(principal, resolver, ct);
        if (caller is null)
        {
            return NoOrg();
        }
        return await Run(() => handler.HandleAsync(caller, new RescheduleAppointmentCommand(
                appointmentId, request.Start, request.MinutesDuration, request.PractitionerFhirId ?? string.Empty,
                request.ConsultationType, request.Description, request.Note), ct),
            ok => ok ? Results.NoContent() : Results.NotFound());
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action, Func<T, IResult> ok)
    {
        try
        {
            return ok(await action());
        }
        catch (AgendaScopeException ex)
        {
            return Results.Problem(title: "Fuera de tus sedes", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Conflicto", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.PreconditionFailed or System.Net.HttpStatusCode.Conflict)
        {
            return Results.Problem(
                title: "La cita cambió",
                detail: "Alguien modificó la cita mientras tanto. Recargá la agenda y volvé a intentar.",
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<AgendaCaller?> CallerAsync(ClaimsPrincipal principal, IPermissionResolver resolver, CancellationToken ct)
    {
        if (!Guid.TryParse(principal.FindFirstValue("organization_id"), out var orgId))
        {
            return null;
        }
        var userRaw = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        if (!Guid.TryParse(userRaw, out var userId))
        {
            return null;
        }
        var roles = principal.FindAll("roles").Select(c => c.Value).ToList();
        var permisos = await resolver.ResolveAsync(orgId, roles, ct);
        return new AgendaCaller(orgId, userId, permisos, principal.FindFirstValue("practitioner_id"));
    }

    private static IResult NoOrg() =>
        Results.Problem(title: "Sin organización", detail: "El token no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);
}

public sealed record ChangeAppointmentStatusRequest(string? Status);

public sealed record RescheduleAppointmentRequest(
    DateTimeOffset Start,
    int MinutesDuration,
    string? PractitionerFhirId,
    string? ConsultationType,
    string? Description,
    string? Note);
