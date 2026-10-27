using System.Security.Claims;
using System.Text.Json;
using IMed.Api.Domain.Authorization;
using IMed.Api.Domain.Encounters;
using IMed.Api.Infrastructure.Persistence;
using IMed.Api.WebApi.Http;
using Microsoft.EntityFrameworkCore;

namespace IMed.Api.WebApi.Endpoints;

/// <summary>
/// Borrador de los cambios a antecedentes de una consulta (Épica P, P-D3). Los cambios se escriben
/// en FHIR al cerrar (P-D1); mientras tanto viven acá para sobrevivir a un refresco o a un cambio
/// de equipo.
///
/// <list type="bullet">
///   <item><c>GET /api/encounters/{id}/history-draft</c> — el borrador vigente (404 si no hay).</item>
///   <item><c>PUT /api/encounters/{id}/history-draft</c> — lo reemplaza; 409 si otra pestaña guardó antes.</item>
///   <item><c>POST /api/encounters/{id}/history-draft/commit</c> — lo marca como escrito; no lo borra.</item>
/// </list>
///
/// <para>Es una tabla de borradores sin reglas de negocio, así que los endpoints hablan con el
/// contexto directamente en vez de pasar por un handler que solo reenviaría.</para>
/// </summary>
public static class EncounterHistoryDraftEndpoints
{
    public static IEndpointRouteBuilder MapEncounterHistoryDraftEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/encounters/{encounterId}/history-draft")
            .WithTags("Encounters")
            .RequireAuthorization(PermissionPolicy.For(PermissionCatalog.RecordWrite));

        group.MapGet("", GetAsync).WithName("GetEncounterHistoryDraft");
        group.MapPut("", SaveAsync).WithName("SaveEncounterHistoryDraft");
        group.MapPost("/commit", CommitAsync).WithName("CommitEncounterHistoryDraft");

        return app;
    }

    public sealed record HistoryDraftDto(
        string EncounterFhirId,
        string PatientFhirId,
        JsonElement Payload,
        int Version,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CommittedAt,
        string? ProvenanceFhirId);

    public sealed record SaveHistoryDraftRequest(string PatientFhirId, JsonElement Payload, int ExpectedVersion);

    public sealed record CommitHistoryDraftRequest(string? ProvenanceFhirId);

    private static async Task<IResult> GetAsync(
        string encounterId, ClaimsPrincipal principal, AppDbContext db, CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }

        var draft = await db.EncounterHistoryDrafts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.EncounterFhirId == encounterId, ct);
        return draft is null ? Results.NotFound() : Results.Ok(ToDto(draft));
    }

    private static async Task<IResult> SaveAsync(
        string encounterId,
        SaveHistoryDraftRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }
        if (request.Payload.ValueKind != JsonValueKind.Object)
        {
            return Results.Problem(title: "Solicitud inválida", detail: "El borrador tiene que ser un objeto JSON.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var draft = await db.EncounterHistoryDrafts
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.EncounterFhirId == encounterId, ct);
        try
        {
            if (draft is null)
            {
                draft = EncounterHistoryDraft.Create(orgId.Value, encounterId, request.PatientFhirId, timeProvider);
                db.EncounterHistoryDrafts.Add(draft);
            }
            else if (draft.PatientFhirId != request.PatientFhirId)
            {
                return Results.Problem(title: "Paciente equivocado",
                    detail: "El borrador de esta consulta es de otro paciente.", statusCode: StatusCodes.Status409Conflict);
            }
            draft.Save(request.Payload.GetRawText(), request.ExpectedVersion, ParseSub(principal), timeProvider);
            await db.SaveChangesAsync(ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(title: "Borrador desactualizado", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return Results.Problem(title: "Solicitud inválida", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (DbUpdateException)
        {
            // Dos pestañas creando el primer borrador a la vez: el índice único deja pasar una.
            return Results.Problem(title: "Borrador desactualizado",
                detail: "Los antecedentes se modificaron desde otra pestaña o equipo. Recargá la consulta.",
                statusCode: StatusCodes.Status409Conflict);
        }
        return Results.Ok(ToDto(draft));
    }

    private static async Task<IResult> CommitAsync(
        string encounterId,
        CommitHistoryDraftRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        var orgId = ParseOrg(principal);
        if (orgId is null) { return ForbiddenOrg(); }

        var draft = await db.EncounterHistoryDrafts
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.EncounterFhirId == encounterId, ct);
        if (draft is null)
        {
            // Una consulta cerrada sin haber tocado antecedentes no tiene borrador: nada que marcar.
            return Results.NoContent();
        }
        draft.Commit(request.ProvenanceFhirId, ParseSub(principal), timeProvider);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static HistoryDraftDto ToDto(EncounterHistoryDraft d)
    {
        using var doc = JsonDocument.Parse(d.PayloadJson);
        return new HistoryDraftDto(
            d.EncounterFhirId, d.PatientFhirId, doc.RootElement.Clone(), d.Version, d.UpdatedAt, d.CommittedAt,
            d.ProvenanceFhirId);
    }

    private static Guid? ParseOrg(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("organization_id"), out var id) ? id : null;

    private static Guid? ParseSub(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub"), out var s)
            ? s : null;

    private static IResult ForbiddenOrg() =>
        Results.Problem(title: "Sin organización", detail: "El JWT no trae organization_id.", statusCode: StatusCodes.Status403Forbidden);
}
