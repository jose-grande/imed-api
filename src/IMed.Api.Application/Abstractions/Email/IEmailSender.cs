namespace IMed.Api.Application.Abstractions.Email;

/// <summary>
/// Port que abstrae el envío de email. La implementación vive en Infrastructure
/// y se elige por config (<c>Email:Provider</c>) — <c>smtp4dev</c> en dev,
/// <c>smtp</c> genérico en otros entornos.
///
/// <para>Este port se usa para los emails que <b>nosotros componemos</b>:
/// notificación a Org Admin sobre nueva join request, notificación al solicitante
/// sobre approve/reject, recordatorios futuros, etc. <b>NO</b> para los emails
/// del flow de invitación de Keycloak — esos los manda KC directamente vía
/// <c>executeActionsEmail</c>, configurado en el realm para apuntar al mismo
/// servidor SMTP (smtp4dev en dev).</para>
/// </summary>
public interface IEmailSender
{
    /// <summary>
    /// Envía el mensaje. Implementaciones SMTP propagan errores de transporte
    /// como excepción — el caller decide si es fatal o si queda en outbox.
    /// </summary>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mensaje a enviar. <see cref="BodyHtml"/> es el shape principal;
/// <see cref="BodyText"/> es opcional (fallback para clientes que no rendean HTML).
/// Si <see cref="BodyText"/> es null, se genera automáticamente strippeando tags
/// de <see cref="BodyHtml"/> en la implementación.
/// </summary>
public sealed record EmailMessage
{
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string BodyHtml { get; init; }
    public string? BodyText { get; init; }
}
