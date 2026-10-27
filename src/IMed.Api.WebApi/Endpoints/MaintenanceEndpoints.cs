using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Allergies;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Authorization;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Operaciones de mantenimiento de datos, por organización y con su propio rastro.
///
/// <list type="bullet">
///   <item><c>POST /api/maintenance/allergies/dedupe?apply=false</c> — reporta (o, con
///   <c>apply=true</c>, marca como error) las alergias duplicadas que dejó la preclínica antes de
///   la Épica P-1.</item>
/// </list>
///
/// <para>Las administra quien administra la organización (<c>org.manage</c>). Corren sobre la
/// organización del token: no hay forma de tocar los datos de otra.</para>
/// </summary>
public static class MaintenanceEndpoints
{
    public const string AuditCategory = "maintenance";

    public static IEndpointRouteBuilder MapMaintenanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/maintenance")
            .WithTags("Maintenance")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.OrgManage));

        group.MapPost("/allergies/dedupe", DedupeAllergiesAsync)
            .WithName("DedupeAllergies")
            .WithSummary("Reporta o marca como error las alergias duplicadas de la organización.");

        return app;
    }

    private static async Task<IResult> DedupeAllergiesAsync(
        bool? apply,
        HttpContext context,
        AllergyDedupeHandler handler,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        if (!Guid.TryParse(context.User.FindFirstValue("organization_id"), out var orgId))
        {
            return Results.Problem(title: "Sin organización", detail: "El JWT no trae organization_id.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        AllergyDedupeReport report;
        try
        {
            report = await handler.HandleAsync(orgId, apply == true, ct);
        }
        catch (FhirVersionConflictException)
        {
            return Results.Problem(
                title: "Una alergia cambió durante la limpieza",
                detail: "Alguien modificó una de las alergias entre la lectura y la escritura; esa tanda no se aplicó. Volvé a correr el reporte.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Aplicar deja rastro siempre; el reporte en seco también, porque lee alergias de todos los pacientes.
        db.AppAuditLog.Add(AppAuditLogEntry.Create(
            category: AuditCategory,
            action: report.Applied ? "Apply:AllergyDedupe" : "Report:AllergyDedupe",
            severity: report.Applied ? AuditSeverity.Warning : AuditSeverity.Info,
            actorUserId: Guid.TryParse(
                context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub"),
                out var sub) ? sub : null,
            organizationId: orgId,
            target: "AllergyIntolerance",
            correlationId: context.TraceIdentifier,
            remoteIp: context.Connection.RemoteIpAddress?.ToString(),
            payloadJson: JsonSerializer.Serialize(new
            {
                scanned = report.AllergiesScanned,
                duplicates = report.DuplicatesFound,
                marked = report.Applied ? report.Groups.SelectMany(g => g.DuplicateIds) : [],
            }),
            timeProvider: timeProvider));
        await db.SaveChangesAsync(ct);

        return Results.Ok(report);
    }
}
