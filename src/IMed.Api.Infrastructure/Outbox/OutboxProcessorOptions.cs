namespace IMed.Api.Infrastructure.Outbox;

/// <summary>Configuración del worker. Bind contra <c>Outbox</c> en appsettings.</summary>
public sealed class OutboxProcessorOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Intervalo de polling cuando NO hay eventos pendientes (segundos).</summary>
    public int IdlePollSeconds { get; set; } = 5;

    /// <summary>Intervalo cuando ya se despachó algo en este tick — agresivo (segundos).</summary>
    public int BusyPollSeconds { get; set; } = 1;

    /// <summary>Cantidad máxima de eventos por tick (batch size).</summary>
    public int BatchSize { get; set; } = 25;

    /// <summary>Backoff base en segundos. Backoff real = <c>BackoffBaseSeconds * 2^(attempt-1)</c>.</summary>
    public int BackoffBaseSeconds { get; set; } = 5;

    /// <summary>Tope del backoff (segundos). Default = 1 hora.</summary>
    public int BackoffMaxSeconds { get; set; } = 3600;

    /// <summary>Tras N intentos fallidos, marca el evento como permanently failed.</summary>
    public int MaxAttempts { get; set; } = 24;

    /// <summary>Si está deshabilitado, el worker no consume (solo se popula la tabla).</summary>
    public bool Enabled { get; set; } = true;
}
