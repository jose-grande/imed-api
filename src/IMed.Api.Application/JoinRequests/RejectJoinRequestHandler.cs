using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.JoinRequests;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.JoinRequests;

public sealed class RejectJoinRequestHandler(
    IJoinRequestsRepository joinRequestsRepo,
    IEmailSender emailSender,
    IAppAuditLog auditLog,
    ILogger<RejectJoinRequestHandler> logger,
    TimeProvider timeProvider)
{
    public async Task HandleAsync(
        RejectJoinRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        var joinRequest = await joinRequestsRepo.GetByIdAsync(command.JoinRequestId, cancellationToken)
            ?? throw new JoinRequestConflictException(
                $"JoinRequest {command.JoinRequestId} no existe.",
                code: "not_found");

        if (joinRequest.OrganizationId != command.OrganizationId)
        {
            throw new JoinRequestConflictException(
                $"JoinRequest {command.JoinRequestId} no existe.",
                code: "not_found");
        }

        if (joinRequest.Status != JoinRequestStatus.Pending)
        {
            throw new JoinRequestConflictException(
                $"No se puede rechazar JoinRequest en estado {joinRequest.Status}.",
                code: "invalid_status");
        }

        joinRequest.Reject(command.Reason, command.ResolvedBy, timeProvider);
        await joinRequestsRepo.UpdateAsync(joinRequest, cancellationToken);

        var notified = await TryNotifyApplicantRejectedAsync(joinRequest, command.Reason, cancellationToken);

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "join_requests",
            action: "rejected",
            severity: AuditSeverity.Info,
            actorUserId: command.ResolvedBy,
            organizationId: command.OrganizationId,
            target: $"JoinRequest:{joinRequest.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                joinRequest.Email,
                Reason = command.Reason,
                ApplicantNotified = notified
            }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "JoinRequest {Id} rechazada por {ResolvedBy}, reason='{Reason}', notified={Notified}",
            joinRequest.Id, command.ResolvedBy, command.Reason, notified);
    }

    private async Task<bool> TryNotifyApplicantRejectedAsync(
        JoinRequest joinRequest,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var reasonBlock = string.IsNullOrWhiteSpace(reason)
                ? string.Empty
                : $"<p><strong>Motivo:</strong></p><blockquote>{System.Net.WebUtility.HtmlEncode(reason)}</blockquote>";

            var html = $$"""
                <!doctype html>
                <html>
                  <body style="font-family: -apple-system, sans-serif; max-width: 560px; margin: 0 auto;">
                    <h2 style="color: #2c3e50;">Tu solicitud no fue aprobada</h2>
                    <p>Hola {{System.Net.WebUtility.HtmlEncode(joinRequest.FullName)}},</p>
                    <p>El administrador no aprobó tu solicitud de ingreso en este momento.</p>
                    {{reasonBlock}}
                    <p>Si crees que es un error, podés contactar directamente a la organización.</p>
                    <hr style="border:none;border-top:1px solid #eee;margin:24px 0;" />
                    <p style="color:#888;font-size:12px;">IMed — Sistema EMD</p>
                  </body>
                </html>
                """;
            await emailSender.SendAsync(new EmailMessage
            {
                To = joinRequest.Email,
                Subject = "Tu solicitud de ingreso — IMed",
                BodyHtml = html
            }, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "JoinRequest {Id}: notificación de rechazo al solicitante falló.",
                joinRequest.Id);
            return false;
        }
    }
}
