using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Application.Invitations;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.Invitations;
using IMed.Api.Domain.JoinRequests;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.JoinRequests;

/// <summary>
/// Handler de <c>POST /api/join-requests/{id}/approve</c>. Aprobar una request
/// reusa el flow de <see cref="CreateInvitationHandler"/> (Épica F3) — emite
/// una invitation con role default <c>practitioner</c> y dispara
/// <c>executeActionsEmail</c> en KC. Linkea la invitation al join request via
/// <c>LinkedInvitationId</c>.
///
/// <para>Además, mandamos un email "tu solicitud fue aprobada" al solicitante
/// como cortesía — KC le va a llegar después con el link de activación.</para>
/// </summary>
public sealed class ApproveJoinRequestHandler(
    IJoinRequestsRepository joinRequestsRepo,
    CreateInvitationHandler createInvitationHandler,
    IEmailSender emailSender,
    IAppAuditLog auditLog,
    ILogger<ApproveJoinRequestHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<ApproveJoinRequestResult> HandleAsync(
        ApproveJoinRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var joinRequest = await joinRequestsRepo.GetByIdAsync(command.JoinRequestId, cancellationToken)
            ?? throw new JoinRequestConflictException(
                $"JoinRequest {command.JoinRequestId} no existe.",
                code: "not_found");

        if (joinRequest.OrganizationId != command.OrganizationId)
        {
            // Aislamiento cross-tenant: 404 en lugar de 403.
            throw new JoinRequestConflictException(
                $"JoinRequest {command.JoinRequestId} no existe.",
                code: "not_found");
        }

        if (joinRequest.Status != JoinRequestStatus.Pending)
        {
            throw new JoinRequestConflictException(
                $"No se puede aprobar JoinRequest en estado {joinRequest.Status}.",
                code: "invalid_status");
        }

        // Reusa el flow de invitation. El admin elige el role al aprobar
        // (UI muestra picker); default = practitioner si no lo manda.
        CreateInvitationResult invitationResult;
        try
        {
            invitationResult = await createInvitationHandler.HandleAsync(new CreateInvitationCommand(
                OrganizationId: command.OrganizationId,
                CreatedBy: command.ResolvedBy,
                Email: joinRequest.Email,
                Role: command.Role,
                SpecialtyHint: null,
                LocationIds: command.LocationIds), cancellationToken);
        }
        catch (InvitationConflictException ex)
        {
            // El flow de invitation detectó un conflicto (ej: el email ya tiene
            // KC user en otra org). Propagamos como JoinRequest conflict para
            // que el endpoint devuelva 409 con código apropiado.
            throw new JoinRequestConflictException(
                $"No se pudo aprobar la solicitud: {ex.Message}",
                code: ex.Code);
        }

        joinRequest.Approve(invitationResult.InvitationId, command.ResolvedBy, timeProvider);
        await joinRequestsRepo.UpdateAsync(joinRequest, cancellationToken);

        var applicantNotified = await TryNotifyApplicantApprovedAsync(joinRequest, cancellationToken);

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "join_requests",
            action: "approved",
            severity: AuditSeverity.Info,
            actorUserId: command.ResolvedBy,
            organizationId: command.OrganizationId,
            target: $"JoinRequest:{joinRequest.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                joinRequest.Email,
                LinkedInvitationId = invitationResult.InvitationId,
                ApplicantNotified = applicantNotified
            }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "JoinRequest {Id} aprobada → Invitation {InvitationId}",
            joinRequest.Id, invitationResult.InvitationId);

        return new ApproveJoinRequestResult(joinRequest.Id, invitationResult.InvitationId, applicantNotified);
    }

    private async Task<bool> TryNotifyApplicantApprovedAsync(
        JoinRequest joinRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            var html = $$"""
                <!doctype html>
                <html>
                  <body style="font-family: -apple-system, sans-serif; max-width: 560px; margin: 0 auto;">
                    <h2 style="color: #2c3e50;">Tu solicitud fue aprobada</h2>
                    <p>Hola {{System.Net.WebUtility.HtmlEncode(joinRequest.FullName)}},</p>
                    <p>El administrador aprobó tu solicitud de ingreso en IMed.</p>
                    <p>En el siguiente email vas a recibir un link para definir tu contraseña y completar tu registro. Si no te llega en los próximos minutos, revisá tu carpeta de spam.</p>
                    <hr style="border:none;border-top:1px solid #eee;margin:24px 0;" />
                    <p style="color:#888;font-size:12px;">IMed — Sistema EMD</p>
                  </body>
                </html>
                """;
            await emailSender.SendAsync(new EmailMessage
            {
                To = joinRequest.Email,
                Subject = "Tu solicitud de ingreso fue aprobada — IMed",
                BodyHtml = html
            }, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "JoinRequest {Id}: email 'aprobada' al solicitante falló (KC le llegará igual con el link de activación).",
                joinRequest.Id);
            return false;
        }
    }
}
