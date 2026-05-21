using IMed.Api.Application.Abstractions.Email;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace IMed.Api.Infrastructure.Email;

/// <summary>
/// Sender SMTP genérico para staging/prod. Usa MailKit con auth + TLS
/// configurable. Se conecta a un relay externo (Mailgun SMTP, SendGrid SMTP,
/// AWS SES SMTP, postfix propio, etc.) — el provider concreto se decide en
/// Épica O (deploy staging).
///
/// <para>Activado cuando <c>Email:Provider="smtp"</c>.</para>
/// </summary>
internal sealed class GenericSmtpEmailSender(
    IOptions<EmailOptions> options,
    ILogger<GenericSmtpEmailSender> logger) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var mime = BuildMime(message);

        using var client = new SmtpClient();
        try
        {
            // SecureSocketOptions:
            //   - StartTls: conecta plain y eleva a TLS (puerto 587 estándar).
            //   - SslOnConnect: TLS desde el saludo (puerto 465).
            //   - Auto: MailKit detecta — usable cuando no estás seguro.
            // UseSsl=true → SslOnConnect; UseSsl=false → StartTls (la mayoría de
            // relays modernos requieren al menos STARTTLS aunque digas "no SSL").
            var socketOptions = _options.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTlsWhenAvailable;

            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken);

            if (!string.IsNullOrEmpty(_options.Username))
            {
                await client.AuthenticateAsync(_options.Username, _options.Password ?? string.Empty, cancellationToken);
            }

            await client.SendAsync(mime, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);

            logger.LogDebug("smtp: enviado a {To} con subject {Subject}", message.To, message.Subject);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "smtp: fallo enviando a {To} via {Host}:{Port}", message.To, _options.Host, _options.Port);
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
