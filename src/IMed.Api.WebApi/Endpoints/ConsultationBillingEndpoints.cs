using System.Security.Claims;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Billing;
using IMed.Api.Domain.Authorization;
using IMed.Api.WebApi.Http;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Cobro de consultas (plan de salida a producción, A2 y A3).
///
/// <list type="bullet">
///   <item><c>GET /api/consultations/type-suggestion</c> — el tipo que se propone al agendar o al
///   iniciar la consulta. Lo usan médicos y recepción, no solo facturación.</item>
///   <item><c>GET/PUT /api/billing/settings</c> — la ventana de "control" (P1).</item>
///   <item><c>POST /api/billing/catalog/specialties/{code}</c> — activa a mano una especialidad: crea
///   sus tres consultas sin precio (P3).</item>
/// </list>
/// </summary>
public static class ConsultationBillingEndpoints
{
    public static IEndpointRouteBuilder MapConsultationBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/consultations/type-suggestion", SuggestAsync)
            .WithTags("Billing · Consultas")
            .WithName("SuggestConsultationType")
            .WithSummary("Tipo de consulta propuesto para un paciente y una especialidad (primera vez o control).")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordSearchDemographics));

        app.MapGet("/api/billing/settings", GetSettingsAsync)
            .WithTags("Billing · Consultas")
            .WithName("GetBillingSettings")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingRead));

        app.MapPut("/api/billing/settings", PutSettingsAsync)
            .WithTags("Billing · Consultas")
            .WithName("UpdateBillingSettings")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        app.MapPost("/api/billing/catalog/specialties/{code}", ActivateSpecialtyAsync)
            .WithTags("Billing · Consultas")
            .WithName("ActivateSpecialty")
            .WithSummary("Crea las tres consultas (primera vez, control, emergencia) de una especialidad, sin precio.")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.BillingSupervise));

        return app;
    }

    private static async Task<IResult> SuggestAsync(
        string patientId, string? specialty, string? practitionerId, string? encounterId, ClaimsPrincipal principal,
        SuggestConsultationTypeHandler handler, PractitionerSpecialtyLookup specialties,
        ILoggerFactory loggers, CancellationToken ct)
    {
        if (OrgOf(principal) is not { } org)
        {
            return Results.Forbid();
        }
        if (string.IsNullOrWhiteSpace(patientId))
        {
            return Results.Problem(title: "Solicitud inválida", detail: "Falta el paciente.", statusCode: 400);
        }
        // Al agendar se sabe el médico, no la especialidad: se usa la suya.
        var especialidad = specialty;
        if (string.IsNullOrWhiteSpace(especialidad) && !string.IsNullOrWhiteSpace(practitionerId))
        {
            try
            {
                especialidad = await specialties.FindAsync(org, practitionerId.Trim(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin HAPI la propuesta cae a Medicina general; es una propuesta, no un cobro.
                loggers.CreateLogger("ConsultationBilling")
                    .LogWarning(ex, "No se pudo leer la especialidad del médico {Practitioner}.", practitionerId);
            }
        }
        return Results.Ok(await handler.HandleAsync(org, patientId.Trim(), especialidad, encounterId, ct));
    }

    private static async Task<IResult> GetSettingsAsync(
        ClaimsPrincipal principal, IBillingSettingsRepository repo, CancellationToken ct)
    {
        if (OrgOf(principal) is not { } org)
        {
            return Results.Forbid();
        }
        var s = await repo.GetAsync(org, ct);
        return Results.Ok(new BillingSettingsDto(s.FollowUpWindowDays));
    }

    private static async Task<IResult> PutSettingsAsync(
        BillingSettingsDto request, ClaimsPrincipal principal, IBillingSettingsRepository repo,
        TimeProvider timeProvider, CancellationToken ct)
    {
        if (OrgOf(principal) is not { } org)
        {
            return Results.Forbid();
        }
        var s = await repo.GetAsync(org, ct);
        try
        {
            s.SetFollowUpWindow(request.FollowUpWindowDays, timeProvider);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: 400);
        }
        await repo.SaveAsync(s, ct);
        return Results.Ok(new BillingSettingsDto(s.FollowUpWindowDays));
    }

    private static async Task<IResult> ActivateSpecialtyAsync(
        string code, ClaimsPrincipal principal, IBillableItemProvisioner provisioner, CancellationToken ct)
    {
        if (OrgOf(principal) is not { } org)
        {
            return Results.Forbid();
        }
        try
        {
            var items = await provisioner.EnsureForSpecialtyAsync(org, code, ct);
            return items.Count == 0
                ? Results.Problem(
                    title: "Sin régimen fiscal",
                    detail: "Configurá primero el régimen fiscal: sin tributos no se puede crear la consulta.",
                    statusCode: StatusCodes.Status409Conflict)
                : Results.Ok(items);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: 400);
        }
    }

    private static Guid? OrgOf(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;
}

public sealed record BillingSettingsDto(int FollowUpWindowDays);
