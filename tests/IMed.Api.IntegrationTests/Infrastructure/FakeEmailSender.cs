using System.Collections.Concurrent;
using IMed.Api.Application.Abstractions.Email;

namespace IMed.Api.IntegrationTests.Infrastructure;

/// <summary>
/// Fake <see cref="IEmailSender"/> para tests de integración. Captura los
/// mensajes enviados sin abrir socket SMTP. Reemplaza el binding de DI vía
/// <c>ImedApiFactory.ConfigureWebHost</c>.
/// </summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly ConcurrentBag<EmailMessage> _sent = new();

    /// <summary>Si true, <see cref="SendAsync"/> tira excepción (simula SMTP caído).</summary>
    public bool SimulateFailure { get; set; }

    public IReadOnlyCollection<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (SimulateFailure)
        {
            throw new InvalidOperationException("Simulated SMTP failure (test fake)");
        }
        _sent.Add(message);
        return Task.CompletedTask;
    }

    public void Reset()
    {
        SimulateFailure = false;
        while (_sent.TryTake(out _)) { }
    }
}
