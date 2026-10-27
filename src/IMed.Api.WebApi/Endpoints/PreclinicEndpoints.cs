using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Preclinic;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Preclínica (Épica P-1). La auxiliar no tiene lectura del expediente (<c>record.read.any</c>) y no
/// debe tenerla; pero para no duplicar alergias necesita ver las ya registradas. Estos endpoints
/// resuelven esa tensión: leen <b>solo</b> al paciente de una cita que está en la fila de
/// preclínica, y escriben la preclínica entera en una transacción.
///
/// <list type="bullet">
///   <item><c>GET /api/preclinic/appointments/{id}</c> — alergias activas y signos vitales ya tomados.</item>
///   <item><c>POST /api/preclinic/appointments/{id}</c> — registra la preclínica.</item>
/// </list>
///
/// <para>Como no pasan por el proxy FHIR, registran su propio acceso en <c>phi_access</c>: que la
/// lectura entre por otra puerta no puede significar que no quede rastro.</para>
/// </summary>
public static class PreclinicEndpoints
{
    public static IEndpointRouteBuilder MapPreclinicEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/preclinic")
            .WithTags("Preclinic")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordPreclinic));

        group.MapGet("/appointments/{appointmentId}", GetContextAsync)
            .WithName("GetPreclinicContext")
            .WithSummary("Alergias registradas y signos vitales ya tomados para la cita.");
        group.MapPost("/appointments/{appointmentId}", SubmitAsync)
            .WithName("SubmitPreclinic")
            .WithSummary("Registra la preclínica de la cita en una sola transacción.");

        return app;
    }

    public sealed record SubmitPreclinicApiRequest(
        PreclinicVitalsInput? Vitals,
        IReadOnlyList<PreclinicCatalogAllergyInput>? CatalogAllergies,
        IReadOnlyList<string>? FreeTextAllergies,
        bool NoKnownAllergies,
        IReadOnlyList<PreclinicMedicationInput>? Medications = null);

    private static async Task<IResult> GetContextAsync(
        string appointmentId,
        HttpContext context,
        GetPreclinicContextHandler handler,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var orgId = ParseOrg(context.User);
        if (orgId is null) { return ForbiddenOrg(); }

        return await RunAsync(async () =>
        {
            var dto = await handler.HandleAsync(orgId.Value, appointmentId, ct);
            await AuditAsync(db, context, orgId.Value, "Read:Preclinic", appointmentId,
                new { patient = dto.PatientFhirId, encounter = dto.EncounterFhirId, allergies = dto.Allergies.Count },
                timeProvider);
            return Results.Ok(dto);
        });
    }

    private static async Task<IResult> SubmitAsync(
        string appointmentId,
        SubmitPreclinicApiRequest request,
        HttpContext context,
        SubmitPreclinicHandler handler,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var orgId = ParseOrg(context.User);
        if (orgId is null) { return ForbiddenOrg(); }

        var practitionerId = context.User.FindFirstValue("practitioner_id");
        return await RunAsync(async () =>
        {
            var result = await handler.HandleAsync(new SubmitPreclinicCommand(
                orgId.Value,
                appointmentId,
                string.IsNullOrWhiteSpace(practitionerId) ? null : practitionerId,
                request.Vitals,
                request.CatalogAllergies,
                request.FreeTextAllergies,
                request.NoKnownAllergies,
                request.Medications), ct);
            await AuditAsync(db, context, orgId.Value, "Create:Preclinic", appointmentId,
                new
                {
                    encounter = result.EncounterFhirId,
                    observations = result.ObservationsCreated,
                    allergies = result.AllergiesCreated,
                    medications = result.MedicationsCreated,
                    skipped = result.SkippedDuplicates.Count,
                    noKnownAllergies = result.NoKnownAllergiesRecorded,
                },
                timeProvider);
            return Results.Ok(result);
        });
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (PreclinicAppointmentNotFoundException ex)
        {
            return Results.Problem(title: "Cita inexistente", detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
        }
        catch (PreclinicAppointmentNotInQueueException ex)
        {
            return Results.Problem(title: "Fuera de la fila de preclínica", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (FhirVersionConflictException)
        {
            return Results.Problem(
                title: "La cita cambió mientras se capturaba",
                detail: "Otra persona modificó la cita o las alergias del paciente. Recargá la preclínica y volvé a guardar.",
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Best-effort, como en el proxy: se pierde una línea de auditoría, no la atención.</summary>
    private static async Task AuditAsync(
        AppDbContext db, HttpContext context, Guid organizationId, string action, string appointmentId,
        object details, TimeProvider timeProvider)
    {
        try
        {
            db.AppAuditLog.Add(AppAuditLogEntry.Create(
                category: FhirProxyEndpoint.PhiAuditCategory,
                action: action,
                severity: AuditSeverity.Info,
                actorUserId: Guid.TryParse(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"),
                    out var sub) ? sub : null,
                organizationId: organizationId,
                target: $"Appointment/{appointmentId}",
                correlationId: context.TraceIdentifier,
                remoteIp: context.Connection.RemoteIpAddress?.ToString(),
                payloadJson: JsonSerializer.Serialize(details),
                timeProvider: timeProvider));
            await db.SaveChangesAsync(context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Preclinic")
                .LogError(ex, "No se pudo registrar el acceso a PHI de la preclínica (best-effort).");
        }
    }

    private static Guid? ParseOrg(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static IResult ForbiddenOrg() =>
        Results.Problem(title: "Sin organización", detail: "El JWT no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);
}
