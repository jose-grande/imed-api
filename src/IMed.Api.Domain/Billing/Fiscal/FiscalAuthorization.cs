namespace IMed.Api.Domain.Billing.Fiscal;

/// <summary>
/// Autorización de numeración emitida por la autoridad fiscal — en Honduras, el CAI del SAR
/// (FSF bloque 1, adaptador HN).
///
/// <para><b>Es el corazón del cumplimiento.</b> Un correlativo duplicado no es un bug cosmético:
/// es incumplimiento fiscal, y dos cajas cobrando a la vez es el caso normal, no el borde. Por eso
/// la asignación del número <b>no vive acá</b>: vive en un <c>UPDATE … RETURNING</c> del
/// repositorio, que es donde Postgres puede garantizar atomicidad. Esta clase guarda el estado y
/// las reglas que se pueden decidir sin tocar la base.</para>
///
/// <para><b>Las cuatro invariantes</b> (FSF §5.2), todas validadas en servidor:</para>
/// <list type="number">
///   <item>No emitir con correlativo mayor que <see cref="RangeTo"/> → agotada, bloquear.</item>
///   <item>No emitir con fecha posterior a <see cref="LimitDate"/> → vencida, bloquear.</item>
///   <item><see cref="NextSequence"/> se asigna atómicamente.</item>
///   <item>Un correlativo asignado <b>jamás</b> se reutiliza, aunque la venta se caiga.</item>
/// </list>
///
/// <para>La cuarta es la que sorprende a quien no facturó nunca: si la impresión falla, el
/// documento ya existe y se reimprime — no se re-emite. Por eso el contador solo sube. Los huecos
/// que eso deja son legítimos y hay que poder explicarlos ante una auditoría, que es distinto de
/// no tenerlos.</para>
/// </summary>
public sealed class FiscalAuthorization
{
    public Guid Id { get; private set; }
    public Guid OrganizationId { get; private set; }

    /// <summary>Sede que emite. Es <c>organization_location</c> (ADR-008), no una tabla aparte.</summary>
    public Guid LocationId { get; private set; }

    public Guid DocumentTypeId { get; private set; }

    /// <summary>Clave de Autorización de Impresión. Se imprime en cada documento.</summary>
    public string Cai { get; private set; } = string.Empty;

    /// <summary>
    /// Punto de emisión: segmento 2 del número (3 dígitos). Varias cajas pueden compartirlo o cada
    /// una tener el suyo — lo decide la configuración, no el código, porque la respuesta depende de
    /// lo que acepte la autoridad y eso se confirma con el contador del cliente.
    /// </summary>
    public string EmissionPointCode { get; private set; } = string.Empty;

    public long RangeFrom { get; private set; }
    public long RangeTo { get; private set; }

    /// <summary>El próximo correlativo a entregar. Solo sube.</summary>
    public long NextSequence { get; private set; }

    /// <summary>Fecha límite de emisión. Pasada esa fecha la autorización no sirve aunque sobren números.</summary>
    public DateOnly LimitDate { get; private set; }

    public FiscalAuthorizationStatus Status { get; private set; }
    public DateTimeOffset IssuedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    private FiscalAuthorization() { }

    public static FiscalAuthorization Create(
        Guid organizationId,
        Guid locationId,
        Guid documentTypeId,
        string cai,
        string emissionPointCode,
        long rangeFrom,
        long rangeTo,
        DateOnly limitDate,
        TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(cai))
        {
            throw new ArgumentException("El CAI es obligatorio.", nameof(cai));
        }

        var point = (emissionPointCode ?? string.Empty).Trim();
        if (point.Length != 3 || !point.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "El punto de emisión son exactamente 3 dígitos.", nameof(emissionPointCode));
        }
        if (rangeFrom < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(rangeFrom), "El rango arranca en 1 o más.");
        }
        if (rangeTo < rangeFrom)
        {
            throw new ArgumentException("El rango termina antes de empezar.", nameof(rangeTo));
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        return new FiscalAuthorization
        {
            Id = Guid.CreateVersion7(now),
            OrganizationId = organizationId,
            LocationId = locationId,
            DocumentTypeId = documentTypeId,
            Cai = cai.Trim().ToUpperInvariant(),
            EmissionPointCode = point,
            RangeFrom = rangeFrom,
            RangeTo = rangeTo,
            NextSequence = rangeFrom,
            LimitDate = limitDate,
            Status = FiscalAuthorizationStatus.Active,
            IssuedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>Cuántos documentos quedan. Cero = agotada.</summary>
    public long Remaining => Math.Max(0, RangeTo - NextSequence + 1);

    /// <summary>Días hasta la fecha límite. Negativo = ya venció.</summary>
    public int DaysUntilLimit(DateOnly today) => LimitDate.DayNumber - today.DayNumber;

    /// <summary>
    /// ¿Se puede emitir hoy con esta autorización? Es la misma condición que aplica el
    /// <c>UPDATE</c> atómico del repositorio; existe acá para poder avisar <b>antes</b> de que el
    /// cajero tenga al paciente enfrente.
    /// </summary>
    public bool CanIssueOn(DateOnly today) =>
        Status == FiscalAuthorizationStatus.Active
        && NextSequence <= RangeTo
        && today <= LimitDate;

    /// <summary>
    /// Por qué no se puede emitir, o <c>null</c> si sí se puede. Se distinguen los motivos porque
    /// cada uno manda al usuario a un lado distinto: agotada → pedir rango nuevo; vencida →
    /// renovar; dada de baja → hablar con administración.
    /// </summary>
    public FiscalReservationFailure? BlockedReason(DateOnly today)
    {
        if (Status == FiscalAuthorizationStatus.Revoked)
        {
            return FiscalReservationFailure.Revoked;
        }
        if (today > LimitDate)
        {
            return FiscalReservationFailure.Expired;
        }
        if (NextSequence > RangeTo)
        {
            return FiscalReservationFailure.Exhausted;
        }
        return null;
    }

    /// <summary>
    /// Da de baja la autorización. No devuelve los correlativos ya entregados: los documentos
    /// emitidos con ellos existen y siguen siendo válidos.
    /// </summary>
    public void Revoke(TimeProvider? timeProvider = null)
    {
        Status = FiscalAuthorizationStatus.Revoked;
        UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Marca el estado que corresponde a la fecha dada. Sirve para que la pantalla muestre
    /// "vencida" sin esperar a que alguien intente emitir. No toca las dadas de baja.
    /// </summary>
    public void RefreshStatus(DateOnly today, TimeProvider? timeProvider = null)
    {
        if (Status == FiscalAuthorizationStatus.Revoked)
        {
            return;
        }

        var nuevo = BlockedReason(today) switch
        {
            FiscalReservationFailure.Expired => FiscalAuthorizationStatus.Expired,
            FiscalReservationFailure.Exhausted => FiscalAuthorizationStatus.Exhausted,
            _ => FiscalAuthorizationStatus.Active,
        };

        if (nuevo != Status)
        {
            Status = nuevo;
            UpdatedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
        }
    }
}
