namespace IMed.Api.Infrastructure.Email;

/// <summary>
/// Config bound a la sección <c>Email:</c> de appsettings.
///
/// <para>Valores típicos:</para>
/// <list type="bullet">
///   <item><b>Dev</b> (smtp4dev): <c>Provider="smtp4dev"</c>, <c>Host="localhost"</c>, <c>Port=2525</c>, <c>UseSsl=false</c>, sin credenciales.</item>
///   <item><b>Staging/prod</b>: <c>Provider="smtp"</c>, host/port/credenciales del relay externo (Mailgun, SendGrid SMTP, AWS SES SMTP, etc.).</item>
/// </list>
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// "smtp4dev" o "smtp". Selecciona el adapter en <c>AddEmail()</c>.
    /// El default vacío hace fallar el bootstrap con mensaje claro — preferible a
    /// que un error de config quede latente hasta el primer envío.
    /// </summary>
    public string Provider { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool UseSsl { get; set; }

    /// <summary>Username/password sólo si el SMTP los requiere (dev sin auth, prod sí).</summary>
    public string? Username { get; set; }
    public string? Password { get; set; }

    public string FromAddress { get; set; } = string.Empty;
    public string FromDisplayName { get; set; } = string.Empty;
}
