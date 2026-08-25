namespace IMed.Api.Domain.Billing.Cash;

/// <summary>
/// Un turno de caja: desde que el cajero declara con cuánto arranca hasta que cuenta y cierra
/// (FSF §7.2).
///
/// <para><b>🔴 El conteo es a ciegas.</b> <see cref="ExpectedAmount"/> se calcula y se guarda
/// <b>al cerrar</b>, después de que el cajero declaró lo que contó. Casi todos los sistemas
/// administrativos locales muestran el esperado en la misma pantalla del conteo, y con eso el
/// arqueo deja de valer: si el número está a la vista, lo que se registra es ese número, no lo que
/// había en la gaveta. Es una decisión deliberada y hay que defenderla cuando alguien pida
/// "mostrarlo para agilizar".</para>
///
/// <para><b>El traspaso no cierra la caja.</b> Cerrar el turno de A y abrir el de B sobre la misma
/// caja es lo normal en un cambio de guardia; obligar a cerrar la caja física haría que el cajero
/// entrante arranque contando lo que el saliente ya contó.</para>
/// </summary>
public sealed class CashSession
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CashPointId { get; private set; }
    public Guid CashierUserId { get; private set; }

    /// <summary>Con cuánto efectivo arrancó, declarado por el cajero.</summary>
    public decimal OpeningAmount { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }

    /// <summary>Lo que el cajero contó. Null mientras el turno está abierto.</summary>
    public decimal? CountedAmount { get; private set; }

    /// <summary>
    /// Lo que debería haber. <b>Se calcula al cerrar</b>, nunca antes: exponerlo durante el turno
    /// convierte el conteo en una transcripción.
    /// </summary>
    public decimal? ExpectedAmount { get; private set; }

    /// <summary>Contado menos esperado. Negativo = falta dinero.</summary>
    public decimal? Difference { get; private set; }

    /// <summary>Explicación de la diferencia, obligatoria cuando no es cero.</summary>
    public string? DifferenceNote { get; private set; }

    /// <summary>Quién autorizó cerrar con una diferencia sobre el tope del cajero.</summary>
    public Guid? DifferenceAuthorizedBy { get; private set; }

    public CashSessionStatus Status { get; private set; }

    /// <summary>Turno anterior de la misma caja, en un traspaso de cajero.</summary>
    public Guid? PreviousSessionId { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    private CashSession() { }

    public static CashSession Open(
        Guid organizationId,
        Guid cashPointId,
        Guid cashierUserId,
        decimal openingAmount,
        Guid? previousSessionId = null,
        TimeProvider? timeProvider = null)
    {
        if (openingAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(openingAmount), "El monto inicial no puede ser negativo.");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CashSession
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            CashPointId = cashPointId,
            CashierUserId = cashierUserId,
            OpeningAmount = openingAmount,
            OpenedAt = now,
            Status = CashSessionStatus.Open,
            PreviousSessionId = previousSessionId,
            UpdatedAt = now,
        };
    }

    public bool IsOpen => Status == CashSessionStatus.Open;

    /// <summary>
    /// Cierra el turno con lo contado. El esperado llega desde afuera —lo calcula quien sabe sumar
    /// pagos y movimientos— y recién acá se guarda.
    /// </summary>
    /// <param name="countedAmount">Lo que el cajero contó en la gaveta.</param>
    /// <param name="expectedAmount">Lo que debería haber, calculado en este momento.</param>
    /// <param name="note">Explicación de la diferencia. Obligatoria si la hay.</param>
    /// <param name="authorizedBy">Supervisor que autorizó, si la diferencia pasó el tope.</param>
    public void Close(
        decimal countedAmount,
        decimal expectedAmount,
        string? note = null,
        Guid? authorizedBy = null,
        TimeProvider? timeProvider = null)
    {
        if (Status == CashSessionStatus.Closed)
        {
            throw new InvalidOperationException("El turno ya está cerrado.");
        }
        if (countedAmount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(countedAmount), "Lo contado no puede ser negativo.");
        }

        var diferencia = countedAmount - expectedAmount;

        // Una diferencia sin explicación es un descuadre que nadie va a poder reconstruir después:
        // al día siguiente ya nadie recuerda si faltaron 200 por un vuelto mal dado o por un
        // billete falso.
        if (diferencia != 0 && string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException(
                "Una diferencia de arqueo exige explicación.", nameof(note));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        CountedAmount = countedAmount;
        ExpectedAmount = expectedAmount;
        Difference = diferencia;
        DifferenceNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        DifferenceAuthorizedBy = authorizedBy;
        Status = CashSessionStatus.Closed;
        ClosedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>
/// Efectivo que entra o sale de la caja sin ser una venta: retiros a bóveda, reposición de
/// sencillo, ajustes.
///
/// <para>Existe porque una caja no debe acumular L 200,000 esperando el cierre, y porque sin
/// registrar el retiro el arqueo daría un faltante enorme que nadie puede explicar.</para>
/// </summary>
public sealed class CashMovement
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid CashSessionId { get; private set; }
    public CashMovementKind Kind { get; private set; }

    /// <summary>Siempre positivo. El signo lo pone <see cref="Kind"/>.</summary>
    public decimal Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;
    public Guid PerformedBy { get; private set; }

    /// <summary>Supervisor que autorizó, cuando el movimiento pasó el tope de quien lo hizo.</summary>
    public Guid? AuthorizedBy { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    private CashMovement() { }

    public static CashMovement Create(
        Guid organizationId,
        Guid cashSessionId,
        CashMovementKind kind,
        decimal amount,
        string reason,
        Guid performedBy,
        Guid? authorizedBy = null,
        TimeProvider? timeProvider = null)
    {
        if (amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), "El monto del movimiento tiene que ser positivo.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException(
                "Un movimiento de caja exige motivo: sin él, el arqueo no se puede reconstruir.",
                nameof(reason));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new CashMovement
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            CashSessionId = cashSessionId,
            Kind = kind,
            Amount = amount,
            Reason = reason.Trim(),
            PerformedBy = performedBy,
            AuthorizedBy = authorizedBy,
            OccurredAt = now,
        };
    }

    /// <summary>Cuánto suma o resta al efectivo esperado.</summary>
    public decimal SignedAmount => Kind switch
    {
        CashMovementKind.Withdrawal => -Amount,
        CashMovementKind.Deposit => Amount,
        _ => Amount,
    };
}
