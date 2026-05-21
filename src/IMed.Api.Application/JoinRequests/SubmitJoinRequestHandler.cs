using System.Text.Json;
using IMed.Api.Application.Abstractions;
using IMed.Api.Application.Abstractions.Email;
using IMed.Api.Application.Profile;
using IMed.Api.Domain.Auditing;
using IMed.Api.Domain.JoinRequests;
using Microsoft.Extensions.Logging;

namespace IMed.Api.Application.JoinRequests;

/// <summary>
/// Handler del endpoint público <c>POST /api/join-requests</c>. Sin autenticación;
/// confía en el rate limiting al borde y en la idempotency (índice único parcial)
/// para resistir spam básico.
///
/// <para>Pasos:</para>
/// <list type="number">
///   <item>Lookup org por FHIR id; si no existe → 404.</item>
///   <item>Idempotency: ¿ya hay request pending para email+org? → 409.</item>
///   <item>Persistir join request + audit log.</item>
///   <item>Best-effort: notificar al founder/Org Admin via <see cref="IEmailSender"/>.
///   Si falla, NO bloqueamos el submit (la request queda en BD para que el admin
///   la vea al entrar a su panel).</item>
/// </list>
/// </summary>
public sealed class SubmitJoinRequestHandler(
    IJoinRequestsRepository joinRequestsRepo,
    IOrganizationProfileRepository orgRepo,
    IKeycloakAdminClient keycloakAdmin,
    IEmailSender emailSender,
    IAppAuditLog auditLog,
    JoinRequestEmailOptions emailOptions,
    ILogger<SubmitJoinRequestHandler> logger,
    TimeProvider timeProvider)
{
    public async Task<SubmitJoinRequestResult> HandleAsync(
        SubmitJoinRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Email))
        {
            throw new ArgumentException("Email es obligatorio.", nameof(command));
        }
        if (string.IsNullOrWhiteSpace(command.FullName))
        {
            throw new ArgumentException("FullName es obligatorio.", nameof(command));
        }
        if (string.IsNullOrWhiteSpace(command.OrganizationFhirId))
        {
            throw new ArgumentException("OrganizationFhirId es obligatorio.", nameof(command));
        }

        var org = await orgRepo.GetByFhirIdAsync(command.OrganizationFhirId, cancellationToken)
            ?? throw new JoinRequestConflictException(
                $"La organización solicitada no existe.",
                code: "organization_not_found");

        var email = command.Email.Trim().ToLowerInvariant();

        var existing = await joinRequestsRepo.GetPendingByEmailAndOrganizationAsync(
            email, org.Id, cancellationToken);
        if (existing is not null)
        {
            throw new JoinRequestConflictException(
                "Ya enviaste una solicitud de ingreso para esta organización; el admin la está revisando.",
                code: "join_request_pending_exists");
        }

        var joinRequest = JoinRequest.Create(
            email: email,
            fullName: command.FullName,
            organizationId: org.Id,
            organizationFhirId: org.FhirId,
            message: command.Message,
            timeProvider: timeProvider);

        await joinRequestsRepo.AddAsync(joinRequest, cancellationToken);

        var adminNotified = await TryNotifyAdminAsync(joinRequest, org, cancellationToken);

        await auditLog.RecordAsync(AppAuditLogEntry.Create(
            category: "join_requests",
            action: "submitted",
            severity: AuditSeverity.Info,
            actorUserId: null, // anónimo
            organizationId: org.Id,
            target: $"JoinRequest:{joinRequest.Id}",
            payloadJson: JsonSerializer.Serialize(new
            {
                joinRequest.Email,
                joinRequest.FullName,
                AdminNotified = adminNotified
            }),
            timeProvider: timeProvider), cancellationToken);

        logger.LogInformation(
            "JoinRequest submitted {Id} email={Email} org={OrgId} adminNotified={Notified}",
            joinRequest.Id, email, org.Id, adminNotified);

        return new SubmitJoinRequestResult(joinRequest.Id, adminNotified);
    }

    private async Task<bool> TryNotifyAdminAsync(
        JoinRequest joinRequest,
        OrganizationProfile org,
        CancellationToken cancellationToken)
    {
        try
        {
            var founderId = await orgRepo.GetFounderUserIdAsync(org.Id, cancellationToken);
            if (founderId is null)
            {
                logger.LogWarning(
                    "JoinRequest {Id}: no se encontró founder para org {OrgId}; admin no notificado.",
                    joinRequest.Id, org.Id);
                return false;
            }

            var founder = await keycloakAdmin.GetUserByIdAsync(founderId.Value, cancellationToken);
            if (founder is null || string.IsNullOrWhiteSpace(founder.Email))
            {
                logger.LogWarning(
                    "JoinRequest {Id}: founder {FounderId} no tiene email en KC; admin no notificado.",
                    joinRequest.Id, founderId);
                return false;
            }

            var html = BuildAdminNotificationHtml(joinRequest, org);
            await emailSender.SendAsync(new EmailMessage
            {
                To = founder.Email,
                Subject = $"Nueva solicitud de ingreso a {org.Name}",
                BodyHtml = html
            }, cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "JoinRequest {Id}: notificación al admin falló; la request queda persistida igual.",
                joinRequest.Id);
            return false;
        }
    }

    private string BuildAdminNotificationHtml(JoinRequest joinRequest, OrganizationProfile org)
    {
        var teamUrl = string.IsNullOrWhiteSpace(emailOptions.TeamPageUrl)
            ? "(configurar Invitations:Email:TeamPageUrl)"
            : emailOptions.TeamPageUrl;

        var msgBlock = string.IsNullOrWhiteSpace(joinRequest.Message)
            ? string.Empty
            : $"<p><strong>Mensaje:</strong></p><blockquote>{System.Net.WebUtility.HtmlEncode(joinRequest.Message)}</blockquote>";

        return $$"""
            <!doctype html>
            <html>
              <body style="font-family: -apple-system, sans-serif; max-width: 560px; margin: 0 auto;">
                <h2 style="color: #2c3e50;">Nueva solicitud de ingreso</h2>
                <p>Una persona pidió unirse a <strong>{{System.Net.WebUtility.HtmlEncode(org.Name)}}</strong> en IMed.</p>
                <ul>
                  <li><strong>Nombre:</strong> {{System.Net.WebUtility.HtmlEncode(joinRequest.FullName)}}</li>
                  <li><strong>Email:</strong> {{System.Net.WebUtility.HtmlEncode(joinRequest.Email)}}</li>
                </ul>
                {{msgBlock}}
                <p>Aprobá o rechazá la solicitud desde tu panel de equipo:</p>
                <p><a href="{{teamUrl}}" style="background:#2c3e50;color:white;padding:10px 16px;text-decoration:none;border-radius:4px;">Ir al panel de equipo</a></p>
                <hr style="border:none;border-top:1px solid #eee;margin:24px 0;" />
                <p style="color:#888;font-size:12px;">IMed — Sistema EMD</p>
              </body>
            </html>
            """;
    }
}

/// <summary>
/// Configuración de los emails que <b>nosotros</b> mandamos en el flow de
/// join requests (no de los que manda KC vía <c>executeActionsEmail</c>).
/// </summary>
public sealed record JoinRequestEmailOptions
{
    public const string SectionName = "JoinRequests:Email";

    /// <summary>
    /// URL del panel "Mi equipo" en el frontend, que se incluye en el email al
    /// admin como CTA. Si null, se inserta un placeholder.
    /// </summary>
    public string? TeamPageUrl { get; init; }
}
