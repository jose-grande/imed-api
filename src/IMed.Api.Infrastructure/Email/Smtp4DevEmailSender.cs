using IMed.Api.Application.Abstractions.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IMed.Api.Infrastructure.Email;

/// <summary>
/// Sender para <a href="https://github.com/rnwood/smtp4dev">smtp4dev</a> local
/// en dev. Sin auth, sin TLS — smtp4dev acepta cualquier conexión y captura
/// los mails para inspección via su UI web.
///
/// <para>Activado cuando <c>Email:Provider="smtp4dev"</c>. En staging/prod se
/// usa <see cref="GenericSmtpEmailSender"/>.</para>
/// </summary>
internal sealed class Smtp4DevEmailSender(
    IOptions<EmailOptions> options,
    ILogger<Smtp4DevEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var mime = BuildMime(message);

        using var client = new SmtpClient();
        try
        {
            // smtp4dev en dev acepta None (plain TCP) sin TLS ni auth.
            await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.None, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            logger.LogDebug("smtp4dev: enviado a {To} con subject {Subject}", message.To, message.Subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "smtp4dev: fallo enviando a {To}. Verificar que smtp4dev esté corriendo en {Host}:{Port}.",
                message.To, _options.Host, _options.Port);
            throw;
        }
    }

    private MimeMessage BuildMime(EmailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromDisplayName, _options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;

        var builder = new BodyBuilder
        {
            HtmlBody = message.BodyHtml,
            TextBody = message.BodyText ?? StripTags(message.BodyHtml),
        };
        mime.Body = builder.ToMessageBody();
        return mime;
    }

    /// <summary>
    /// Fallback minimal — strippea tags HTML para producir un body plano.
    /// No es renderer real (no maneja entities, links, etc.); es solo para
    /// que clientes text-only no vean basura. Si necesitamos algo serio,
    /// usamos un renderer dedicado (HtmlAgilityPack o equivalente).
    /// </summary>
    private static string StripTags(string html)
    {
        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty)
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Trim();
    }
}
